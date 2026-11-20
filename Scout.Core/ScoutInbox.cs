using Doggo;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Scout.Core;

public sealed record HandoffReceipt(string BatchId, List<string> RunIds, int NewFiles);

public sealed class ScoutInbox
{
    readonly ScoutDatabase database;
    readonly string workspaceRoot;
    readonly RoutedHandoffs transport;
    public ScoutInbox(ScoutDatabase database, string workspaceRoot)
    {
        if (!database.UsesDoggo) throw new InvalidOperationException("Scout inbox requires prepared Doggo storage.");
        if (!string.Equals(ScoutStorage.For(workspaceRoot)?.Path, database.Path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Scout workspaces must use the same Doggo database as the inbox.");
        this.database = database;
        this.workspaceRoot = Path.GetFullPath(workspaceRoot);
        transport = new(database.Path);
        using var db = new Doggo.Sqlite(database.Path);
        db.Exec("BEGIN IMMEDIATE");
        try
        {
            RoutedHandoffs.EnsureVersion(db, "scout_handoffs");
            db.Exec("CREATE TABLE IF NOT EXISTS scout_handoff_receipts(batch_id TEXT PRIMARY KEY REFERENCES handoff_routed_batches(id),digest TEXT NOT NULL,payload TEXT NOT NULL,accepted_utc TEXT NOT NULL)");
            db.Exec("CREATE TABLE IF NOT EXISTS scout_handoff_files(source_key TEXT PRIMARY KEY,metadata TEXT NOT NULL,run_id TEXT NOT NULL,batch_id TEXT NOT NULL REFERENCES scout_handoff_receipts(batch_id))");
            db.Exec("COMMIT");
        }
        catch { db.Exec("ROLLBACK"); throw; }
    }
    public List<RoutedBatch> Items() => transport.Inbox("scout");
    public HandoffReceipt Accept(string id) => database.AcceptHandoff(id, workspaceRoot);
    public void Decline(string id) => transport.Decline(id, "scout");
}

public sealed partial class ScoutDatabase
{
    internal HandoffReceipt AcceptHandoff(string id, string workspaceRoot)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        List<string[]> Query(string sql, params string[] values)
        {
            using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
            for (var i = 0; i < values.Length; i++) cmd.Parameters.AddWithValue("$p" + i, values[i]);
            using var reader = cmd.ExecuteReader(); var rows = new List<string[]>();
            while (reader.Read()) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetString(i)).ToArray());
            return rows;
        }
        void Execute(string sql, params string[] values)
        {
            using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
            for (var i = 0; i < values.Length; i++) cmd.Parameters.AddWithValue("$p" + i, values[i]);
            cmd.ExecuteNonQuery();
        }
        var offered = Query("SELECT digest,payload,status FROM handoff_routed_batches WHERE id=$p0 AND source_app='tucker' AND target_app='scout' AND contract='tucker.scout.outputs' AND version=1", id);
        if (offered.Count != 1) throw new InvalidDataException("No supported handoff addressed to Scout was found.");
        var row = offered[0];
        var batch = ScoutHandoff.Read(id, row[0], row[1]);
        var saved = Query("SELECT digest,payload FROM scout_handoff_receipts WHERE batch_id=$p0", id);
        if (saved.Count > 0)
        {
            if (saved[0][0] != row[0] || row[2] != "Accepted") throw new InvalidDataException("The handoff receipt is inconsistent.");
            // Media may have moved during a later Scout run. The receipt remains authoritative.
            return JsonSerializer.Deserialize<HandoffReceipt>(saved[0][1])!;
        }
        if (row[2] != "Offered") throw new InvalidOperationException("This handoff is no longer waiting for acceptance.");
        var fresh = new List<PublishedOutput>(); var runIds = new HashSet<string>();
        static string Metadata(PublishedOutput file) => JsonSerializer.Serialize(file with { JobId = "" });
        foreach (var file in batch.Files)
        {
            var existing = Query("SELECT metadata,run_id FROM scout_handoff_files WHERE source_key=$p0", file.Key);
            if (existing.Count > 0)
            {
                if (existing[0][0] != Metadata(file)) throw new InvalidDataException("This output was already accepted with different metadata: " + file.Path);
                runIds.Add(existing[0][1]);
            }
            else { file.ValidateFile(); fresh.Add(file); }
        }
        var assignments = new List<(PublishedOutput File, string RunId)>();
        // One Scout has one series and one filesystem root. Its saved selection is exact.
        foreach (var group in fresh.GroupBy(f => (Series: f.Series?.Provider + ":" + f.Series?.Id,
            Root: System.IO.Path.GetPathRoot(f.Path)!.ToUpperInvariant())))
        {
            var files = group.ToList(); var first = files[0];
            var library = System.IO.Path.GetDirectoryName(first.Path)!;
            while (files.Any(f => !PreviewPlanner.Within(f.Path, library)))
                library = System.IO.Path.GetDirectoryName(library) ?? throw new InvalidDataException("Outputs need a common folder.");
            SeriesIdentity? identity = first.Series is {} s ? new(s.Provider, s.Id, s.Name, s.Year, s.Overview) : null;
            identity?.Validate();
            var run = new RunWorkspace
            {
                DestinationMode = "default",
                Name = identity?.Name ?? batch.RunName, Library = library, SeriesIdentity = identity, SourceHandoffId = id,
                SelectedFiles = files.Select(f => f.Path).ToList(), SelectionScope = new("files"),
                Setup = new ScoutSetup { Mode = identity == null ? "discover" : "known", MetadataProvider = identity?.Provider ?? "tvdb",
                    Query = identity?.Name ?? "", CandidateKey = identity?.Key },
                Events = [$"Accepted {files.Count} compressed outputs from Tucker: {batch.RunName} (handoff {batch.Id}). Waiting for Start Scout."]
            };
            var artifact = System.IO.Path.Combine(workspaceRoot, run.Id, "workspace.json");
            Write(db, tx, artifact, JsonSerializer.SerializeToUtf8Bytes(run));
            runIds.Add(run.Id);
            assignments.AddRange(files.Select(f => (f, run.Id)));
        }
        var receipt = new HandoffReceipt(id, runIds.Order(StringComparer.Ordinal).ToList(), fresh.Count);
        var now = DateTime.UtcNow.ToString("O");
        Execute("INSERT INTO scout_handoff_receipts VALUES($p0,$p1,$p2,$p3)", id, row[0], JsonSerializer.Serialize(receipt), now);
        foreach (var (file, runId) in assignments)
            Execute("INSERT INTO scout_handoff_files VALUES($p0,$p1,$p2,$p3)", file.Key, Metadata(file), runId, id);
        Execute("UPDATE handoff_routed_batches SET status='Accepted',response_utc=$p0 WHERE id=$p1 AND status='Offered'", now, id);
        Execute("INSERT INTO handoff_routed_events(batch_id,actor,kind,created_utc) VALUES($p0,'scout','Accepted',$p1)", id, now);
        tx.Commit();
        return receipt;
    }
}
