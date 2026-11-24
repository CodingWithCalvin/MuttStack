using System.IO;
using Doggo;

namespace Tucker;

public sealed record ScoutDelivery(string Id, string RunId, string Status, int Files);

public sealed class ScoutOutbox
{
    readonly string path;
    readonly RoutedHandoffs transport;
    public ScoutOutbox(string databasePath)
    {
        path = databasePath;
        transport = new(path);
        using var db = new Sqlite(path);
        db.Exec("BEGIN IMMEDIATE");
        try
        {
            RoutedHandoffs.EnsureVersion(db, "tucker_handoffs");
            db.Exec("CREATE TABLE IF NOT EXISTS tucker_scout_outbox(id TEXT PRIMARY KEY,run_id TEXT NOT NULL,digest TEXT NOT NULL,payload TEXT NOT NULL,created_utc TEXT NOT NULL)");
            db.Exec("CREATE TABLE IF NOT EXISTS tucker_scout_sent_files(job_id TEXT PRIMARY KEY,source_key TEXT NOT NULL UNIQUE,batch_id TEXT NOT NULL REFERENCES tucker_scout_outbox(id))");
            db.Exec("COMMIT");
        }
        catch { db.Exec("ROLLBACK"); throw; }
    }
    public bool HasUnsent(IEnumerable<Job> jobs)
    {
        using var db = new Sqlite(path);
        var sent = db.Query("SELECT job_id FROM tucker_scout_sent_files").Select(r => r[0]).ToHashSet();
        return jobs.Any(j => j.Status == "Completed" && j.Encoding != null && !sent.Contains(j.Id));
    }
    public string? SendCompleted(string runId, IEnumerable<Job> jobs)
    {
        // Retrying an interrupted delivery always uses the exact persisted payload.
        RetryPending();
        using var db = new Sqlite(path);
        db.Exec("BEGIN IMMEDIATE");
        string? id = null;
        try
        {
            var sent = db.Query("SELECT job_id,source_key FROM tucker_scout_sent_files");
            var jobIds = sent.Select(r => r[0]).ToHashSet();
            var keys = sent.Select(r => r[1]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var files = new List<PublishedOutput>();
            string runName = "";
            foreach (var job in jobs.Where(j => j.QueueId == runId && j.Status == "Completed" && j.Encoding != null && !jobIds.Contains(j.Id)))
            {
                var output = System.IO.Path.GetFullPath(job.OutputPath);
                if (string.Equals(output, System.IO.Path.GetFullPath(job.Source), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Only published compressed outputs can be sent to Scout.");
                var info = new FileInfo(output);
                if (!info.Exists) throw new FileNotFoundException("Compressed output is unavailable.", output);
                var series = job.Series is {} s ? new HandoffSeries(s.Provider, s.Id, s.Name, s.Year, s.Overview) : null;
                var file = new PublishedOutput(job.Id, output, info.Length, info.LastWriteTimeUtc, series,
                    job.Season, job.VolumeNumber, job.DiscNumber, job.DiscLabel, job.RipDate, job.OriginalFilename);
                if (!keys.Add(file.Key)) continue;
                file.ValidateFile();
                files.Add(file); jobIds.Add(job.Id); runName = job.QueueName;
            }
            if (files.Count > 0)
            {
                id = Guid.NewGuid().ToString("N");
                var batch = new ScoutHandoff(id, runId, runName, files);
                var payload = batch.Serialize(); var digest = ScoutHandoff.Digest(payload);
                ScoutHandoff.Read(id, digest, payload);
                db.Exec("INSERT INTO tucker_scout_outbox VALUES(?,?,?,?,?)", id, runId, digest, payload, DateTime.UtcNow.ToString("O"));
                foreach (var file in files) db.Exec("INSERT INTO tucker_scout_sent_files VALUES(?,?,?)", file.JobId, file.Key, id);
            }
            db.Exec("COMMIT");
        }
        catch { db.Exec("ROLLBACK"); throw; }
        RetryPending();
        return id;
    }
    public void RetryPending()
    {
        List<string[]> pending;
        using (var db = new Sqlite(path))
            pending = db.Query("SELECT o.id,o.digest,o.payload FROM tucker_scout_outbox o LEFT JOIN handoff_routed_batches h ON h.id=o.id WHERE h.id IS NULL OR h.digest<>o.digest OR h.payload<>o.payload OR h.target_app<>'scout' OR h.source_app<>'tucker' OR h.contract<>'tucker.scout.outputs' OR h.version<>1");
        foreach (var row in pending) transport.Offer(row[0], row[1], row[2]);
    }
    public List<ScoutDelivery> Deliveries(string runId)
    {
        using var db = new Sqlite(path);
        return db.Query("SELECT o.id,o.run_id,COALESCE(h.status,'Not delivered'),(SELECT COUNT(*) FROM tucker_scout_sent_files f WHERE f.batch_id=o.id) FROM tucker_scout_outbox o LEFT JOIN handoff_routed_batches h ON h.id=o.id AND h.digest=o.digest AND h.target_app='scout' WHERE o.run_id=? ORDER BY o.created_utc", runId)
            .Select(r => new ScoutDelivery(r[0], r[1], r[2], int.Parse(r[3]))).ToList();
    }
}
