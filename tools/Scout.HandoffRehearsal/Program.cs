using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Doggo;
using Microsoft.Data.Sqlite;
using Scout.Core;
using Tucker;

// Synthetic media only. Every phase runs in a separate process against a marked copy.
if (args.Length < 2) throw new ArgumentException("Usage: <prepare|send|accept|append|verify> <isolated-root> [offline-doggo.migrated.db]");
var mode = args[0]; var root = Path.GetFullPath(args[1]);
if (!Path.IsPathFullyQualified(args[1]) || !(root.Contains("\\.build\\handoff-rehearsal-", StringComparison.OrdinalIgnoreCase) ||
    mode is "selection-handoff" or "selection-status" && root.Contains("\\.build\\cutover-rehearsal-", StringComparison.OrdinalIgnoreCase)))
    throw new ArgumentException("Use a new .build/handoff-rehearsal-* directory, never an installed app directory.");
for (var ancestor = new DirectoryInfo(root); ancestor != null; ancestor = ancestor.Parent)
    if (ancestor.Exists && ancestor.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Rehearsal paths cannot traverse junctions.");
var databasePath = Path.Combine(root, "doggo.db");
var marker = Path.Combine(root, "isolated-handoff-rehearsal.json");
const string RunId = "synthetic-tucker-scout-rehearsal";
void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); }
void WriteJson(string name, object value) => File.WriteAllText(Path.Combine(root, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
if (mode is "selection-handoff" or "selection-status")
{
    var choice = ScoutStorageSelection.Resolve(Path.Combine(root, "Scout"));
    Check(choice.UsesDoggo && Path.GetFullPath(choice.DatabasePath).Equals(Path.Combine(root, "Doggo", "doggo.db"), StringComparison.OrdinalIgnoreCase), "Persistent selector resolves only the isolated shared database without environment overrides");
    const string selectedRun = "persistent-selection-rehearsal";
    var sender = new ScoutOutbox(choice.DatabasePath);
    if (mode == "selection-status")
    {
        Check(sender.Deliveries(selectedRun).Single().Status == "Accepted", "Separate sender process restores Scout acceptance through the selected database");
        return;
    }
    Check(sender.Deliveries(selectedRun).Count == 0, "No duplicate selected-storage fixture has been sent");
    var output = Path.Combine(root, "synthetic-output"); Directory.CreateDirectory(output);
    var job = new Job { QueueId = selectedRun, QueueName = "Isolated Example", Source = Path.Combine(root, "synthetic-raw.mkv"),
        Name = "published.mkv", Status = "Completed", Encoding = new("unused", "fixture", "", false, output, "mkv"), Series = new("tvdb", "42", "Isolated Example", "2020", "Synthetic") };
    File.WriteAllText(job.Source, "synthetic raw"); File.WriteAllText(job.OutputPath, "synthetic compressed output");
    var id = sender.SendCompleted(selectedRun, [job])!;
    var database = ScoutDatabase.OpenExistingDoggo(choice.DatabasePath); ScoutStorage.Register(choice.WorkspacesRoot, database);
    try
    {
        var inbox = new ScoutInbox(database, choice.WorkspacesRoot); var receipt = inbox.Accept(id);
        var run = new WorkspaceStore(choice.WorkspacesRoot).ReadAll(includeArchived: true).Single(r => r.Id == receipt.RunIds.Single());
        Check(run.Status == "Draft" && run.SelectedFiles.SequenceEqual(new[] { job.OutputPath }), "Scout accepts into exact-file waiting history through the persistent selection");
        Check(inbox.Accept(id).RunIds.SequenceEqual(receipt.RunIds), "Selected-storage acceptance remains idempotent");
    }
    finally { ScoutStorage.Unregister(choice.WorkspacesRoot); }
    WriteJson("selected-handoff-report.json", new { Passed = true, EnvironmentOverrideRequired = false, LiveAppsUpdated = false, Database = choice.DatabasePath });
    return;
}
SqliteConnection Open(string path, bool readOnly = false)
{
    var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()); db.Open(); return db;
}
Dictionary<string, List<string>> Rows(string path)
{
    using var db = Open(path, true); using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
    var names = new List<string>(); using (var reader = cmd.ExecuteReader()) while (reader.Read()) names.Add(reader.GetString(0));
    var result = new Dictionary<string, List<string>>();
    foreach (var name in names)
    {
        cmd.CommandText = "SELECT * FROM \"" + name.Replace("\"", "\"\"") + "\"";
        using var reader = cmd.ExecuteReader(); var hashes = new List<string>();
        while (reader.Read())
        {
            var row = Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray();
            hashes.Add(Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(row))));
        }
        hashes.Sort(StringComparer.Ordinal); result[name] = hashes;
    }
    return result;
}
if (mode == "prepare")
{
    if (args.Length != 3 || Directory.Exists(root)) throw new ArgumentException("Preparation needs an unused directory and an offline migration rehearsal snapshot.");
    var source = Path.GetFullPath(args[2]);
    if (!source.Contains("\\.build\\doggo-rehearsal-", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(source) != "doggo.migrated.db")
        throw new ArgumentException("Only an existing isolated migration rehearsal snapshot is accepted.");
    Directory.CreateDirectory(root);
    var sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
    using (var original = Open(source, true)) using (var copy = Open(databasePath)) original.BackupDatabase(copy);
    WriteJson("baseline.json", Rows(databasePath));
    WriteJson("isolated-handoff-rehearsal.json", new { Version = 1, Source = source, SourceHash = sourceHash });
    Check(sourceHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))), "Offline source snapshot was not modified");
    Console.WriteLine("Prepared " + databasePath); return;
}
if (!File.Exists(marker) || !File.Exists(databasePath)) throw new InvalidOperationException("Run prepare first. A rehearsal marker and copied database are required.");
if (mode == "send")
{
    var database = new DoggoDatabase(databasePath); using var sql = new Doggo.Sqlite(databasePath);
    Check(sql.Query("SELECT id FROM tucker_jobs WHERE queue_id=?", RunId).Count == 0, "Synthetic run has not already been seeded");
    var output = Path.Combine(root, "synthetic-media"); Directory.CreateDirectory(output);
    var jobs = new List<Job>();
    for (var index = 0; index < 3; index++)
    {
        var raw = Path.Combine(root, "synthetic-raw-" + index + ".mkv"); File.WriteAllText(raw, "synthetic raw fixture " + index);
        var job = new Job { QueueId = RunId, QueueName = "Alpha and Beta", Source = raw, Name = "compressed-" + index + ".mkv",
            Encoding = new("unused", "fixture", "", false, output, "mkv"), Status = index == 2 ? "Waiting" : "Completed",
            Series = index == 1 ? new("tmdb", "202", "Beta", "2021", "Second show") : new("tvdb", "101", "Alpha", "2020", "First show"),
            Season = index == 1 ? "03" : "02", VolumeNumber = "1", DiscNumber = "2", DiscLabel = "Synthetic Disc", OriginalFilename = "title" + index + ".mkv", RipDate = new(2026, 1, 2) };
        if (job.Status == "Completed") File.WriteAllText(job.OutputPath, "synthetic compressed output " + index);
        var ordinal = int.Parse(sql.Query("SELECT COALESCE(MAX(ordinal),-1)+1 FROM tucker_jobs")[0][0]);
        sql.Exec("INSERT INTO tucker_jobs VALUES(?,?,?,?)", job.Id, ordinal.ToString(), RunId, JsonSerializer.Serialize(job)); jobs.Add(job);
    }
    var outbox = new ScoutOutbox(databasePath); var id = outbox.SendCompleted(RunId, jobs);
    Check(id != null && outbox.Deliveries(RunId).Single() is { Status: "Offered", Files: 2 }, "Sender offers only two completed outputs while third remains waiting");
    var baseline = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(Path.Combine(root, "baseline.json")))!;
    Check(Rows(databasePath)["handoff_batches"].SequenceEqual(baseline["handoff_batches"]), "Legacy Ripley to Tucker inbox remains unchanged");
    return;
}
if (mode == "accept")
{
    var db = ScoutDatabase.OpenExistingDoggo(databasePath); var store = new WorkspaceStore(Path.Combine(root, "scout-workspaces")); ScoutStorage.Register(store.Root, db);
    try
    {
        var inbox = new ScoutInbox(db, store.Root);
        foreach (var batch in inbox.Items().Where(b => b.Status == "Offered" && b.Read().RunId == RunId))
        {
            var receipt = inbox.Accept(batch.Id); var again = inbox.Accept(batch.Id);
            Check(receipt.RunIds.SequenceEqual(again.RunIds), "Receiver accepts once and returns the same receipt on retry");
        }
        var runs = store.ReadAll();
        Check(runs.Count > 0 && runs.All(r => r.Status == "Draft" && r.SelectedFiles.Count == 1 && r.SelectionScope?.Kind == "files" && r.Report == ""), "Accepted Scouts retain exact file scope and never start automatically");
    }
    finally { ScoutStorage.Unregister(store.Root); }
    return;
}
if (mode == "append")
{
    var db = new DoggoDatabase(databasePath); var jobs = db.Read().Jobs.Where(j => j.QueueId == RunId).ToList();
    var outbox = new ScoutOutbox(databasePath);
    Check(outbox.Deliveries(RunId).Single().Status == "Accepted", "Reopened sender learns acceptance recorded while it was closed");
    var addition = jobs.Single(j => j.Status == "Waiting"); File.WriteAllText(addition.OutputPath, "synthetic later completed output"); addition.Status = "Completed";
    using var sql = new Doggo.Sqlite(databasePath); sql.Exec("UPDATE tucker_jobs SET payload=? WHERE id=?", JsonSerializer.Serialize(addition), addition.Id);
    outbox.SendCompleted(RunId, jobs);
    Check(outbox.Deliveries(RunId).Count == 2 && outbox.Deliveries(RunId).Single(d => d.Status == "Offered").Files == 1, "Later completion offers a fresh batch without duplicating accepted outputs");
    return;
}
if (mode == "verify")
{
    var baseline = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(Path.Combine(root, "baseline.json")))!;
    var current = Rows(databasePath);
    foreach (var (table, hashes) in baseline)
    {
        Check(current.TryGetValue(table, out var rows) && hashes.GroupBy(h => h).All(g => rows.Count(h => h == g.Key) >= g.Count()), "Existing " + table + " rows remain byte-for-byte equivalent");
        if (table is not ("doggo_migrations" or "tucker_jobs" or "scout_documents")) Check(current[table].Count == hashes.Count, table + " has no unintended new rows");
    }
    var outbox = new ScoutOutbox(databasePath); var deliveries = outbox.Deliveries(RunId);
    Check(deliveries.Count == 2 && deliveries.All(d => d.Status == "Accepted") && deliveries.Sum(d => d.Files) == 3, "Sender restores both acceptances after another process restart");
    Check(outbox.SendCompleted(RunId, new DoggoDatabase(databasePath).Read().Jobs.Where(j => j.QueueId == RunId)) == null, "Resending the completed run creates no duplicates");
    using var sql = new Doggo.Sqlite(databasePath);
    Check(sql.Query("PRAGMA integrity_check")[0][0] == "ok" && sql.Query("PRAGMA foreign_key_check").Count == 0, "Rehearsal database integrity and foreign keys pass");
    var original = JsonDocument.Parse(File.ReadAllText(marker)).RootElement;
    Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(original.GetProperty("Source").GetString()!))) == original.GetProperty("SourceHash").GetString(), "Offline migration snapshot still has its original hash");
    WriteJson("handoff-report.json", new { Passed = true, Database = databasePath, ExistingTablesPreserved = baseline.Count, AcceptedBatches = 2, AcceptedFiles = 3,
        SeparateSenderReceiverProcesses = true, LiveDatabaseSwitched = false, InstalledAppsUpdated = false });
    Console.WriteLine("Report: " + Path.Combine(root, "handoff-report.json")); return;
}
throw new ArgumentException("Unknown rehearsal phase.");
