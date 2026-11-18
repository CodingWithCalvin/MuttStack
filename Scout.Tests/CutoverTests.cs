using Doggo;
using Scout.Core;
using System.Text;
using System.Text.Json;

static class CutoverTests
{
    sealed class Fixture : IDisposable
    {
        public string Root, ScoutRoot, Source, Target, Settings, Workspaces, Plan = "";
        public Fixture(string temp)
        {
            Root = Path.Combine(temp, "cutover-" + Guid.NewGuid().ToString("N")); ScoutRoot = Path.Combine(Root, "Scout");
            Source = Path.Combine(ScoutRoot, "scout.db"); Target = Path.Combine(Root, "Doggo", "doggo.db");
            Settings = Path.Combine(ScoutRoot, "UI-Trial"); Workspaces = Path.Combine(Settings, "Workspaces");
            var db = new ScoutDatabase(Source); _ = new DoggoInbox(Target);
            db.Write(Path.Combine(Settings, "settings.json"), "{\"LocalModel\":\"retained\"}"u8.ToArray());
            db.Write(Path.Combine(Settings, "credentials.dpapi"), WindowsSecrets.Protect("{\"metadata|tvdb\":\"old-test-key\",\"metadata|tvdb-pin\":\"test-pin\"}"u8.ToArray()));
            var run = new RunWorkspace { Name = "Retained Scout", Library = Root, Status = "Finished", Archived = true };
            db.Write(Path.Combine(Workspaces, run.Id, "workspace.json"), JsonSerializer.SerializeToUtf8Bytes(run));
            db.Write(Path.Combine(Workspaces, run.Id, "report.json"), "{\"files\":[{\"path\":\"synthetic.mkv\",\"evidence\":\"kept\"}]}"u8.ToArray());
            using var sql = new Sqlite(Target); sql.Exec("CREATE TABLE peer_records(id TEXT PRIMARY KEY,value TEXT)"); sql.Exec("INSERT INTO peer_records VALUES('original','keep')");
        }
        public void Prepare() => Plan = ScoutDatabaseCutover.Prepare(ScoutRoot, Source, Target, Path.Combine(Root, "prepared"), Settings, Workspaces);
        public void Apply(Action<string>? checkpoint = null) => ScoutDatabaseCutover.Apply(Plan, checkpoint: checkpoint);
        public void Dispose() { if (File.Exists(Source)) File.SetAttributes(Source, File.GetAttributes(Source) & ~FileAttributes.ReadOnly); }
    }
    static void Check(bool ok) { if (!ok) throw new Exception("Cutover assertion failed."); }
    static void Reject(Action action)
    {
        try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or Microsoft.Data.Sqlite.SqliteException or System.Security.Cryptography.CryptographicException) { return; }
        throw new Exception("Unsafe operation succeeded.");
    }
    public static void Run(Action<string, Action> test, string temp)
    {
        void Scenario(string name, Action<Fixture> action) => test(name, () => { using var f = new Fixture(temp); action(f); });
        Scenario("Persistent selection preserves defaults and process overrides never rewrite it", f =>
        {
            Check(!ScoutStorageSelection.Resolve(f.ScoutRoot).UsesDoggo && !File.Exists(ScoutStorageSelection.ConfigPath(f.ScoutRoot)));
            f.Prepare(); f.Apply(); var before = File.ReadAllBytes(ScoutStorageSelection.ConfigPath(f.ScoutRoot));
            var selected = ScoutStorageSelection.Resolve(f.ScoutRoot); Check(selected.UsesDoggo && selected.DatabasePath == f.Target && selected.SettingsRoot == f.Settings);
            var overridden = ScoutStorageSelection.Resolve(f.ScoutRoot, doggoOverride: f.Target, settingsOverride: Path.Combine(f.Root, "other-settings"));
            Check(overridden.SettingsRoot != selected.SettingsRoot && File.ReadAllBytes(ScoutStorageSelection.ConfigPath(f.ScoutRoot)).SequenceEqual(before));
        });
        Scenario("Cutover preserves history reports credentials and peers and retires the legacy writer", f =>
        {
            var before = ScoutDatabaseCutover.Hash(f.Source); var old = new ScoutDatabase(f.Source);
            var keys = old.Read(Path.Combine(f.Settings, "credentials.dpapi"));
            f.Prepare(); Check(ScoutDatabaseCutover.Hash(f.Source) == before && !File.Exists(ScoutStorageSelection.ConfigPath(f.ScoutRoot)));
            f.Apply(); var db = ScoutDatabase.OpenExistingDoggo(ScoutStorageSelection.Resolve(f.ScoutRoot).DatabasePath);
            Check(db.Read(Path.Combine(f.Settings, "credentials.dpapi")).SequenceEqual(keys) && ScoutDatabaseCutover.Hash(f.Source) == before);
            Check(File.GetAttributes(f.Source).HasFlag(FileAttributes.ReadOnly));
            ScoutStorage.Register(f.Workspaces, db);
            try { Check(new WorkspaceStore(f.Workspaces).ReadAll(includeArchived: true).Single().Archived); }
            finally { ScoutStorage.Unregister(f.Workspaces); }
            using var sql = new Sqlite(f.Target); Check(sql.Query("SELECT value FROM peer_records")[0][0] == "keep");
            f.Apply(); Check(sql.Query("SELECT * FROM doggo_cutovers").Count == 1);
        });
        Scenario("Credential readers follow persisted Doggo selection and key removal never falls back", f =>
        {
            Check(ScoutCredentialReader.ReadTvdb(f.ScoutRoot) == ("old-test-key", "test-pin")); f.Prepare(); f.Apply();
            var db = ScoutDatabase.OpenExistingDoggo(f.Target);
            db.Write(Path.Combine(f.Settings, "credentials.dpapi"), WindowsSecrets.Protect("{\"metadata|tvdb\":\"new-test-key\"}"u8.ToArray()));
            Check(ScoutCredentialReader.ReadTvdb(f.ScoutRoot) == ("new-test-key", null));
            db.Write(Path.Combine(f.Settings, "credentials.dpapi"), WindowsSecrets.Protect("{}"u8.ToArray()));
            Reject(() => ScoutCredentialReader.ReadTvdb(f.ScoutRoot));
            using var sql = new Sqlite(f.Target); sql.Exec("DELETE FROM scout_documents WHERE kind='credentials.dpapi'");
            Reject(() => ScoutCredentialReader.ReadTvdb(f.ScoutRoot));
        });
        Scenario("Missing selected database and corrupt or future selectors fail without falling back", f =>
        {
            f.Prepare(); f.Apply(); File.Move(f.Target, f.Target + ".offline");
            Reject(() => ScoutStorageSelection.Resolve(f.ScoutRoot)); Reject(() => ScoutCredentialReader.ReadTvdb(f.ScoutRoot)); Check(!File.Exists(f.Target));
            File.Move(f.Target + ".offline", f.Target); File.WriteAllText(ScoutStorageSelection.ConfigPath(f.ScoutRoot), "{"); Reject(() => ScoutStorageSelection.Resolve(f.ScoutRoot));
            File.WriteAllText(ScoutStorageSelection.ConfigPath(f.ScoutRoot), JsonSerializer.Serialize(new ScoutStorageChoice(2, "Doggo", f.Target, f.Settings, f.Workspaces)));
            Reject(() => ScoutCredentialReader.ReadTvdb(f.ScoutRoot));
        });
        foreach (var stage in new[] { "before-import", "during-import", "after-import", "before-selection", "after-selection" })
        {
            Scenario("Interrupted cutover resumes safely at " + stage, f =>
            {
                f.Prepare(); Reject(() => f.Apply(s => { if (s == stage) throw new IOException("simulated interruption"); }));
                Check(File.Exists(ScoutStorageSelection.PendingPath(f.ScoutRoot))); Reject(() => ScoutStorageSelection.Resolve(f.ScoutRoot)); Reject(() => ScoutCredentialReader.ReadTvdb(f.ScoutRoot));
                f.Apply(); Check(ScoutStorageSelection.Resolve(f.ScoutRoot).UsesDoggo && !File.Exists(ScoutStorageSelection.PendingPath(f.ScoutRoot)));
                using var sql = new Sqlite(f.Target); Check(sql.Query("SELECT * FROM scout_imports").Count == 1);
            });
            Scenario("Interrupted cutover rolls back safely at " + stage, f =>
            {
                f.Prepare(); Reject(() => f.Apply(s => { if (s == stage) throw new IOException("simulated interruption"); }));
                ScoutDatabaseCutover.Rollback(f.Plan); ScoutDatabaseCutover.Rollback(f.Plan);
                Check(!ScoutStorageSelection.Resolve(f.ScoutRoot).UsesDoggo && !File.GetAttributes(f.Source).HasFlag(FileAttributes.ReadOnly));
                Check(ScoutCredentialReader.ReadTvdb(f.ScoutRoot).Key == "old-test-key");
                using var sql = new Sqlite(f.Target); Check(sql.Query("SELECT name FROM sqlite_master WHERE name GLOB 'scout_*'").Count == 0);
            });
        }
        foreach (var which in new[] { "source", "target", "snapshot", "stage", "selection" })
            Scenario("Cutover rejects stale or changed " + which, f =>
            {
                f.Prepare();
                if (which == "selection") ScoutStorageSelection.Save(f.ScoutRoot, new(1, "Standalone", f.Source, f.Settings, f.Workspaces));
                else
                {
                    var file = which switch { "source" => f.Source, "target" => f.Target, "snapshot" => Path.Combine(f.Root, "prepared", "scout.snapshot.db"), _ => Path.Combine(f.Root, "prepared", "doggo.migrated.db") };
                    using var sql = new Sqlite(file); sql.Exec("CREATE TABLE changed(value TEXT)");
                }
                Reject(() => f.Apply()); Check(!File.Exists(ScoutStorageSelection.PendingPath(f.ScoutRoot)));
                using var target = new Sqlite(f.Target); Check(target.Query("SELECT name FROM sqlite_master WHERE name='scout_documents'").Count == 0);
            });
        Scenario("Rollback preserves new peer data and refuses new Scout work", f =>
        {
            f.Prepare(); Reject(() => f.Apply(s => { if (s == "after-import") throw new IOException("interrupted"); }));
            using var sql = new Sqlite(f.Target); sql.Exec("INSERT INTO peer_records VALUES('later','retained')");
            ScoutDatabaseCutover.Rollback(f.Plan); Check(sql.Query("SELECT * FROM peer_records").Count == 2);
        });
        Scenario("Rollback after Scout edits refuses data loss and leaves selected storage active", f =>
        {
            f.Prepare(); f.Apply(); var db = ScoutDatabase.OpenExistingDoggo(f.Target); db.Write(Path.Combine(f.Settings, "later.json"), "{}"u8.ToArray());
            Reject(() => ScoutDatabaseCutover.Rollback(f.Plan)); Check(ScoutStorageSelection.Resolve(f.ScoutRoot).UsesDoggo && db.Exists(Path.Combine(f.Settings, "later.json")));
        });
        Scenario("Shared app leases block cutover and exclusive maintenance blocks credentials", f =>
        {
            using (StorageLease.Acquire(false, f.Target)) Reject(() => f.Prepare());
            f.Prepare(); using (StorageLease.Acquire(false, ScoutStorageSelection.ConfigPath(f.ScoutRoot))) Reject(() => f.Apply());
            using (StorageLease.Acquire(true, f.Target)) Reject(() => StorageLease.Acquire(false, f.Target));
            using (StorageLease.Acquire(true, ScoutStorageSelection.ConfigPath(f.ScoutRoot))) Reject(() => ScoutCredentialReader.ReadTvdb(f.ScoutRoot));
            Check(!File.Exists(ScoutStorageSelection.PendingPath(f.ScoutRoot)));
        });
        Scenario("Open legacy writer blocks cutover even without cooperative leases", f =>
        {
            f.Prepare(); using var file = new FileStream(f.Source, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            Reject(() => f.Apply()); Check(!File.Exists(ScoutStorageSelection.PendingPath(f.ScoutRoot)));
        });
        Scenario("Occupied Scout destination and mismatched logical roots are refused", f =>
        {
            Reject(() => ScoutDatabaseCutover.Prepare(f.ScoutRoot, f.Source, f.Target, Path.Combine(f.Root, "wrong-roots"), Path.Combine(f.Root, "wrong"), f.Workspaces));
            _ = new ScoutDatabase(f.Target, true); Reject(() => f.Prepare());
        });
        Scenario("Canonical live paths are rejected before opening any database", f =>
        {
            Reject(() => ScoutDatabaseCutover.Prepare(ScoutStorageSelection.DefaultRoot, Path.Combine(ScoutStorageSelection.DefaultRoot, "scout.db"), f.Target, Path.Combine(f.Root, "blocked"), f.Settings, f.Workspaces));
            Check(!Directory.Exists(Path.Combine(f.Root, "blocked")));
        });
        Scenario("Rollback restores an existing standalone selector exactly", f =>
        {
            ScoutStorageSelection.Save(f.ScoutRoot, new(1, "Standalone", f.Source, f.Settings, f.Workspaces));
            var before = File.ReadAllBytes(ScoutStorageSelection.ConfigPath(f.ScoutRoot));
            f.Prepare(); f.Apply(); ScoutDatabaseCutover.Rollback(f.Plan);
            Check(File.ReadAllBytes(ScoutStorageSelection.ConfigPath(f.ScoutRoot)).SequenceEqual(before));
        });
        Scenario("Recovery refuses an edited plan until its original journal identity is restored", f =>
        {
            f.Prepare(); var original = File.ReadAllBytes(f.Plan);
            Reject(() => f.Apply(s => { if (s == "after-import") throw new IOException("interrupted"); }));
            File.AppendAllText(f.Plan, " "); Reject(() => f.Apply()); Reject(() => ScoutDatabaseCutover.Rollback(f.Plan));
            File.WriteAllBytes(f.Plan, original); f.Apply(); Check(ScoutStorageSelection.Resolve(f.ScoutRoot).UsesDoggo);
        });
    }
}
