using Doggo;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Scout.Core;

public sealed record ScoutCutoverPlan(int Version, string Id, string ScoutRoot, string Source, string Target,
    string Directory, string SettingsRoot, string WorkspacesRoot, string SourceHash, string TargetHash,
    string SnapshotHash, string StagedHash, string ScoutHash, string? PreviousSelection, int SourceAttributes);

public static class ScoutDatabaseCutover
{
    static readonly string[] ScoutTables = ["scout_documents", "scout_file_results", "scout_imports"];
    public static string Prepare(string scoutRoot, string source, string target, string directory,
        string settingsRoot, string workspacesRoot, bool allowLive = false)
    {
        var paths = new[] { scoutRoot, source, target, directory, settingsRoot, workspacesRoot };
        if (paths.Any(p => !Path.IsPathFullyQualified(p))) throw new ArgumentException("Cutover paths must be absolute.");
        scoutRoot = Path.GetFullPath(scoutRoot); source = Path.GetFullPath(source); target = Path.GetFullPath(target); directory = Path.GetFullPath(directory);
        Guard([scoutRoot, source, target, directory], allowLive);
        if (Same(source, target) || Same(source, ScoutStorageSelection.ConfigPath(scoutRoot)) || Same(target, ScoutStorageSelection.ConfigPath(scoutRoot)) ||
            Within(source, directory) || Within(target, directory) || Within(scoutRoot, directory)) throw new ArgumentException("Cutover inputs and backup directory must be separate.");
        if (!File.Exists(source) || !File.Exists(target)) throw new FileNotFoundException("Both existing databases are required.");
        using var lease = Lease(scoutRoot, source, target);
        using var sourceGuard = IdleSource(source);
        var selected = ScoutStorageSelection.Resolve(scoutRoot, settingsRoot);
        if (selected.UsesDoggo || !Same(selected.DatabasePath, source)) throw new InvalidOperationException("Scout must still select the specified standalone source.");
        using (var db = Open(target, true)) { RequireDoggo(db); RequireEmptyScout(db); }
        var before = File.Exists(ScoutStorageSelection.ConfigPath(scoutRoot)) ? Convert.ToBase64String(File.ReadAllBytes(ScoutStorageSelection.ConfigPath(scoutRoot))) : null;
        var sourceHash = Hash(source); var targetHash = Hash(target);
        var migration = ScoutDoggoMigration.Rehearse(source, target, directory);
        if (Hash(source) != sourceHash || Hash(target) != targetHash) throw new IOException("A database changed during preparation. Prepare a fresh plan.");
        // Ensure the logical roots describe the migrated data rather than opening empty history later.
        using (var db = Open(migration.Target, true))
        {
            using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT path,kind FROM scout_documents";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var path = reader.GetString(0); var kind = reader.GetString(1);
                if (kind == "workspace.json" && !Within(path, workspacesRoot) ||
                    kind is "settings.json" or "credentials.dpapi" && !Within(path, settingsRoot))
                    throw new InvalidDataException("The selected logical roots do not cover the saved Scout settings/history. Specify their original roots.");
            }
        }
        var plan = new ScoutCutoverPlan(1, Guid.NewGuid().ToString("N"), scoutRoot, source, target, directory,
            settingsRoot, workspacesRoot, sourceHash, targetHash, Hash(migration.SourceSnapshot), Hash(migration.Target),
            Hash(migration.Target, true), before, (int)File.GetAttributes(source));
        var pathToPlan = Path.Combine(directory, "cutover-plan.json");
        ScoutStorageSelection.AtomicWrite(pathToPlan, JsonSerializer.SerializeToUtf8Bytes(plan, new JsonSerializerOptions { WriteIndented = true }));
        return pathToPlan;
    }

    public static void Apply(string planPath, bool allowLive = false, Action<string>? checkpoint = null)
    {
        var plan = Load(planPath, allowLive);
        using var lease = Lease(plan.ScoutRoot, plan.Source, plan.Target);
        using var sourceGuard = IdleSource(plan.Source);
        VerifyArtifacts(plan); VerifySelection(plan, allowActivated: true); VerifyPending(planPath, plan);
        using (var db = Open(plan.Target))
        {
            using var tx = db.BeginTransaction(deferred: false);
            var state = State(db, tx, plan.Id);
            if (state == "Applied") { tx.Rollback(); Complete(planPath, plan, checkpoint); return; }
            if (state == "RolledBack") throw new InvalidOperationException("This cutover was rolled back. Prepare a fresh plan.");
            if (Hash(db, tx) != plan.TargetHash || Hash(plan.Source) != plan.SourceHash)
                throw new IOException("Scout or Doggo changed after preparation. Prepare fresh backups and a new plan.");
            RequireDoggo(db, tx); RequireEmptyScout(db, tx);
            WritePending(planPath, plan); checkpoint?.Invoke("before-import");
            using var staged = Open(Path.Combine(plan.Directory, "doggo.migrated.db"), true);
            using var stagedTx = staged.BeginTransaction(deferred: true);
            if (Hash(staged, stagedTx) != plan.StagedHash) throw new InvalidDataException("Prepared database changed.");
            // DDL, imported Scout rows, migration version and cutover receipt commit together.
            using (var schema = staged.CreateCommand())
            {
                schema.Transaction = stagedTx;
                schema.CommandText = "SELECT sql FROM sqlite_master WHERE name IN ('scout_documents','scout_file_results','scout_imports','scout_file_source') ORDER BY CASE type WHEN 'table' THEN 0 ELSE 1 END,name";
                using var rows = schema.ExecuteReader(); while (rows.Read()) Exec(db, tx, rows.GetString(0));
            }
            foreach (var table in ScoutTables) Copy(staged, stagedTx, db, tx, table);
            checkpoint?.Invoke("during-import");
            using (var version = staged.CreateCommand())
            {
                version.Transaction = stagedTx; version.CommandText = "SELECT applied_utc FROM doggo_migrations WHERE component='scout' AND version=1";
                Exec(db, tx, "INSERT INTO doggo_migrations VALUES('scout',1,$p0)", (string)version.ExecuteScalar()!);
            }
            if (Hash(db, tx, true) != plan.ScoutHash) throw new InvalidDataException("Imported Scout data differs from the prepared copy.");
            Integrity(db, tx);
            var versionRows = Query(db, tx, "SELECT MAX(version) FROM doggo_migrations WHERE component='doggo_cutover'");
            if (int.TryParse(versionRows[0][0], out var versionNumber) && versionNumber > 1) throw new InvalidDataException("A newer cutover tool is required.");
            Exec(db, tx, "CREATE TABLE IF NOT EXISTS doggo_cutovers(id TEXT PRIMARY KEY,source_hash TEXT NOT NULL,scout_hash TEXT NOT NULL,state TEXT NOT NULL,updated_utc TEXT NOT NULL)");
            Exec(db, tx, "INSERT OR IGNORE INTO doggo_migrations VALUES('doggo_cutover',1,$p0)", DateTime.UtcNow.ToString("O"));
            Exec(db, tx, "INSERT INTO doggo_cutovers VALUES($p0,$p1,$p2,'Applied',$p3)", plan.Id, plan.SourceHash, plan.ScoutHash, DateTime.UtcNow.ToString("O"));
            tx.Commit();
        }
        checkpoint?.Invoke("after-import");
        Complete(planPath, plan, checkpoint);
    }

    static void Complete(string planPath, ScoutCutoverPlan plan, Action<string>? checkpoint)
    {
        if (Hash(plan.Source) != plan.SourceHash) throw new IOException("Standalone Scout changed. Recovery needs review; no data was overwritten.");
        if (!File.Exists(ScoutStorageSelection.PendingPath(plan.ScoutRoot)) && IsActivated(plan)) return;
        if (Hash(plan.Target, true) != plan.ScoutHash) throw new IOException("Scout's imported data changed. Recovery will not overwrite it.");
        WritePending(planPath, plan);
        // Retire the old writer before activating the new selection. Ciphertext and data remain intact.
        File.SetAttributes(plan.Source, (FileAttributes)plan.SourceAttributes | FileAttributes.ReadOnly);
        checkpoint?.Invoke("before-selection");
        ScoutStorageSelection.Save(plan.ScoutRoot, Choice(plan));
        checkpoint?.Invoke("after-selection");
        File.Delete(ScoutStorageSelection.PendingPath(plan.ScoutRoot));
        Report(plan, "Activated");
    }

    public static void Rollback(string planPath, bool allowLive = false)
    {
        var plan = Load(planPath, allowLive);
        using var lease = Lease(plan.ScoutRoot, plan.Source, plan.Target);
        using var sourceGuard = IdleSource(plan.Source);
        VerifyArtifacts(plan); VerifySelection(plan, allowActivated: true); VerifyPending(planPath, plan);
        if (Hash(plan.Source) != plan.SourceHash) throw new IOException("The standalone source changed. Rollback needs review.");
        using (var db = Open(plan.Target))
        {
            using var tx = db.BeginTransaction(deferred: false);
            var state = State(db, tx, plan.Id);
            if (state == "Applied")
            {
                if (Hash(db, tx, true) != plan.ScoutHash) throw new IOException("Scout has new data or schema changes. Rollback is refused to avoid losing work.");
                WritePending(planPath, plan);
                Exec(db, tx, "DROP TABLE scout_file_results; DROP TABLE scout_imports; DROP TABLE scout_documents; DELETE FROM doggo_migrations WHERE component='scout'");
                Exec(db, tx, "UPDATE doggo_cutovers SET state='RolledBack',updated_utc=$p0 WHERE id=$p1", DateTime.UtcNow.ToString("O"), plan.Id);
            }
            else { RequireEmptyScout(db, tx); WritePending(planPath, plan); }
            Integrity(db, tx); tx.Commit();
        }
        if (plan.PreviousSelection == null) File.Delete(ScoutStorageSelection.ConfigPath(plan.ScoutRoot));
        else ScoutStorageSelection.AtomicWrite(ScoutStorageSelection.ConfigPath(plan.ScoutRoot), Convert.FromBase64String(plan.PreviousSelection));
        File.SetAttributes(plan.Source, (FileAttributes)plan.SourceAttributes);
        File.Delete(ScoutStorageSelection.PendingPath(plan.ScoutRoot)); Report(plan, "RolledBack");
    }

    public static ScoutCutoverPlan Load(string path, bool allowLive = false)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Plan path must be absolute.");
        Guard([path], allowLive);
        var plan = JsonSerializer.Deserialize<ScoutCutoverPlan>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("Missing cutover plan.");
        if (plan.Version != 1 || !Guid.TryParseExact(plan.Id, "N", out _) || !Same(path, Path.Combine(plan.Directory, "cutover-plan.json"))) throw new InvalidDataException("Invalid cutover plan.");
        Guard([plan.ScoutRoot, plan.Source, plan.Target, plan.Directory], allowLive);
        if (Same(plan.Source, plan.Target) || Within(plan.Source, plan.Directory) || Within(plan.Target, plan.Directory) || Within(plan.ScoutRoot, plan.Directory)) throw new InvalidDataException("Overlapping cutover paths.");
        ScoutStorageSelection.Validate(Choice(plan));
        return plan;
    }
    static ScoutStorageChoice Choice(ScoutCutoverPlan p) => new(1, "Doggo", p.Target, p.SettingsRoot, p.WorkspacesRoot, p.Id);
    static bool IsActivated(ScoutCutoverPlan p)
    {
        var config = ScoutStorageSelection.ConfigPath(p.ScoutRoot);
        return File.Exists(config) && JsonSerializer.Deserialize<ScoutStorageChoice>(File.ReadAllBytes(config)) == Choice(p);
    }
    static void VerifySelection(ScoutCutoverPlan p, bool allowActivated)
    {
        var file = ScoutStorageSelection.ConfigPath(p.ScoutRoot);
        var actual = File.Exists(file) ? Convert.ToBase64String(File.ReadAllBytes(file)) : null;
        if (actual != p.PreviousSelection && !(allowActivated && IsActivated(p))) throw new IOException("Scout's storage selection changed since preparation.");
    }
    static void VerifyArtifacts(ScoutCutoverPlan p)
    {
        if (Hash(Path.Combine(p.Directory, "scout.snapshot.db")) != p.SnapshotHash || Hash(Path.Combine(p.Directory, "doggo.before.db")) != p.TargetHash ||
            Hash(Path.Combine(p.Directory, "doggo.migrated.db")) != p.StagedHash) throw new InvalidDataException("A prepared backup or database changed. Refusing cutover.");
    }
    static void VerifyPending(string path, ScoutCutoverPlan p)
    {
        var marker = ScoutStorageSelection.PendingPath(p.ScoutRoot);
        if (File.Exists(marker) && File.ReadAllText(marker) != PendingText(path)) throw new IOException("Another or modified cutover is pending. Use its original plan for recovery.");
    }
    static string PendingText(string path) => JsonSerializer.Serialize(new { Plan = Path.GetFullPath(path), Digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) });
    static void WritePending(string path, ScoutCutoverPlan p) => ScoutStorageSelection.AtomicWrite(ScoutStorageSelection.PendingPath(p.ScoutRoot), Encoding.UTF8.GetBytes(PendingText(path)));
    static void Report(ScoutCutoverPlan p, string state) => ScoutStorageSelection.AtomicWrite(Path.Combine(p.Directory, "cutover-result.json"), JsonSerializer.SerializeToUtf8Bytes(new { p.Id, State = state, p.Target, SourceRetained = true, PeerTablesReplaced = false, InstalledAppsUpdated = false }));
    static StorageLease Lease(string root, string source, string target) => StorageLease.Acquire(true, ScoutStorageSelection.ConfigPath(root), source, target);
    static FileStream IdleSource(string source) => new(source, FileMode.Open, FileAccess.Read, FileShare.Read);
    static bool Same(string a, string b) => Path.GetFullPath(a).Equals(Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    static bool Within(string path, string root) => Same(path, root) || Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    static void Guard(IEnumerable<string> paths, bool allowLive)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var path in paths)
        {
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Cutover paths must be absolute.");
            if (!allowLive && new[] { "Scout", "Doggo", "Episcout" }.Any(n => Within(path, Path.Combine(local, n))))
                throw new InvalidOperationException("Live paths require an explicit --allow-live operation. Use isolated copies for testing.");
            if (!allowLive && !Path.GetFullPath(path).Contains("\\.build\\", StringComparison.OrdinalIgnoreCase) && !Within(path, Path.GetTempPath()))
                throw new InvalidOperationException("Without --allow-live, cutover files must be isolated under .build or the temporary directory.");
            for (var part = new DirectoryInfo(path); part != null; part = part.Parent)
                if ((Directory.Exists(part.FullName) || File.Exists(part.FullName)) && File.GetAttributes(part.FullName).HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("Cutover paths cannot traverse symbolic links or junctions.");
        }
        if (allowLive)
            foreach (var name in new[] { "Scout", "Tucker", "Ripley", "Reel", "Episcout" })
                foreach (var process in Process.GetProcessesByName(name)) { using (process) throw new InvalidOperationException("Close all Doggo apps before live cutover: " + name); }
    }
    static SqliteConnection Open(string path, bool readOnly = false)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 5 }.ToString());
        db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL"; cmd.ExecuteNonQuery(); return db;
    }
    static void RequireDoggo(SqliteConnection db, SqliteTransaction? tx = null)
    {
        if (Query(db, tx, "SELECT MAX(version) FROM doggo_migrations WHERE component='doggo'")[0][0] != "1") throw new InvalidDataException("An existing Doggo version 1 database is required.");
    }
    static void RequireEmptyScout(SqliteConnection db, SqliteTransaction? tx = null)
    {
        if (Query(db, tx, "SELECT name FROM sqlite_master WHERE name GLOB 'scout_*'").Count != 0 || Query(db, tx, "SELECT component FROM doggo_migrations WHERE component GLOB 'scout*'").Count != 0)
            throw new InvalidDataException("Doggo already contains Scout data or schema. Refusing to merge or overwrite it.");
    }
    static string? State(SqliteConnection db, SqliteTransaction tx, string id)
    {
        if (Query(db, tx, "SELECT name FROM sqlite_master WHERE name='doggo_cutovers'").Count == 0) return null;
        return Query(db, tx, "SELECT state FROM doggo_cutovers WHERE id=$p0", id).FirstOrDefault()?[0];
    }
    static void Copy(SqliteConnection source, SqliteTransaction sourceTx, SqliteConnection target, SqliteTransaction tx, string table)
    {
        using var read = source.CreateCommand(); read.Transaction = sourceTx; read.CommandText = "SELECT * FROM " + table;
        using var rows = read.ExecuteReader();
        while (rows.Read())
        {
            using var insert = target.CreateCommand(); insert.Transaction = tx;
            insert.CommandText = "INSERT INTO " + table + " VALUES(" + string.Join(',', Enumerable.Range(0, rows.FieldCount).Select(i => "$p" + i)) + ")";
            for (var i = 0; i < rows.FieldCount; i++) insert.Parameters.AddWithValue("$p" + i, rows.GetValue(i)); insert.ExecuteNonQuery();
        }
    }
    static void Exec(SqliteConnection db, SqliteTransaction? tx, string sql, params string[] values)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        for (var i = 0; i < values.Length; i++) cmd.Parameters.AddWithValue("$p" + i, values[i]); cmd.ExecuteNonQuery();
    }
    static List<string[]> Query(SqliteConnection db, SqliteTransaction? tx, string sql, params string[] values)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        for (var i = 0; i < values.Length; i++) cmd.Parameters.AddWithValue("$p" + i, values[i]);
        using var reader = cmd.ExecuteReader(); var result = new List<string[]>();
        while (reader.Read()) result.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!).ToArray());
        return result;
    }
    static void Integrity(SqliteConnection db, SqliteTransaction tx)
    {
        if (Query(db, tx, "PRAGMA integrity_check")[0][0] != "ok" || Query(db, tx, "PRAGMA foreign_key_check").Count != 0) throw new InvalidDataException("Database integrity validation failed.");
    }
    public static string Hash(string path, bool scoutOnly = false)
    {
        using var db = Open(path, true); using var tx = db.BeginTransaction(deferred: true); return Hash(db, tx, scoutOnly);
    }
    static string Hash(SqliteConnection db, SqliteTransaction tx, bool scoutOnly = false)
    {
        var schema = Query(db, tx, "SELECT type,name,tbl_name,sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'" +
            (scoutOnly ? " AND (name GLOB 'scout_*' OR tbl_name GLOB 'scout_*')" : "") + " ORDER BY type,name");
        var parts = new List<string> { JsonSerializer.Serialize(schema) };
        if (!scoutOnly) parts.Add(Query(db, tx, "PRAGMA user_version")[0][0]);
        var tables = schema.Where(r => r[0] == "table").Select(r => r[1]).ToList();
        if (scoutOnly) tables.Add("doggo_migrations");
        foreach (var table in tables)
        {
            using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "SELECT * FROM \"" + table.Replace("\"", "\"\"") + "\"" + (scoutOnly && table == "doggo_migrations" ? " WHERE component GLOB 'scout*'" : "");
            using var rows = cmd.ExecuteReader(); var hashes = new List<string>();
            while (rows.Read())
            {
                var values = Enumerable.Range(0, rows.FieldCount).Select(i => rows.IsDBNull(i) ? null : rows.GetValue(i)).ToArray();
                hashes.Add(Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(values))));
            }
            hashes.Sort(StringComparer.Ordinal); parts.Add(table + ":" + string.Join(',', hashes));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', parts))));
    }
}
