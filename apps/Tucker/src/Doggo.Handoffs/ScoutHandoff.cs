using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Doggo;

public sealed record HandoffSeries(string Provider, string Id, string Name, string Year = "", string Overview = "");
public sealed record PublishedOutput(string JobId, string Path, long Size, DateTime ModifiedUtc,
    HandoffSeries? Series = null, string Season = "", string Volume = "", string Disc = "",
    string DiscLabel = "", DateTime RipDate = default, string OriginalFilename = "")
{
    public string Key => DoggoInbox.SourceKey(Path, Size, ModifiedUtc);
    public void ValidateFile()
    {
        var file = new FileInfo(Path);
        if (!file.Exists || file.Length != Size || file.LastWriteTimeUtc != ModifiedUtc)
            throw new IOException("A compressed output is missing or has changed: " + Path);
    }
}

// A frozen inventory of published outputs, never a request to encode or start Scout.
public sealed record ScoutHandoff(string Id, string RunId, string RunName, List<PublishedOutput> Files,
    int Version = 1, string SourceApp = "tucker", string TargetApp = "scout", string Contract = "tucker.scout.outputs")
{
    public string Serialize() => JsonSerializer.Serialize(this);
    public static string Digest(string payload) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    public static ScoutHandoff Read(string id, string digest, string payload)
    {
        if (Digest(payload) != digest) throw new InvalidDataException("Handoff contents do not match their digest.");
        var batch = JsonSerializer.Deserialize<ScoutHandoff>(payload) ?? throw new InvalidDataException("Missing handoff.");
        if (batch.Id != id || !Guid.TryParse(id, out _) || string.IsNullOrWhiteSpace(batch.RunId) ||
            string.IsNullOrWhiteSpace(batch.RunName) || batch.Version != 1 || batch.SourceApp != "tucker" ||
            batch.TargetApp != "scout" || batch.Contract != "tucker.scout.outputs" || batch.Files is not { Count: > 0 })
            throw new InvalidDataException("Unsupported Tucker to Scout handoff.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var jobs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in batch.Files)
        {
            if (file == null || string.IsNullOrWhiteSpace(file.JobId) || !jobs.Add(file.JobId) ||
                string.IsNullOrWhiteSpace(file.Path) || !System.IO.Path.IsPathFullyQualified(file.Path) || !paths.Add(System.IO.Path.GetFullPath(file.Path)) ||
                !new[] { ".mkv", ".mp4", ".m4v", ".avi" }.Contains(System.IO.Path.GetExtension(file.Path).ToLowerInvariant()) ||
                file.Size <= 0 || file.ModifiedUtc.Kind != DateTimeKind.Utc || file.ModifiedUtc == default ||
                file.Season == null || file.Volume == null || file.Disc == null || file.DiscLabel == null || file.OriginalFilename == null)
                throw new InvalidDataException("Invalid published output in handoff.");
            if (file.Series is {} series && (series.Provider is not ("tvdb" or "tmdb" or "tvmaze") ||
                !long.TryParse(series.Id, out var number) || number <= 0 || string.IsNullOrWhiteSpace(series.Name) || series.Year == null || series.Overview == null))
                throw new InvalidDataException("Invalid series identity in handoff.");
        }
        return batch;
    }
}

public sealed record RoutedBatch(string Id, string Digest, string Payload, string Status)
{
    public ScoutHandoff Read() => ScoutHandoff.Read(Id, Digest, Payload);
    public string Summary { get { var b = Read(); return $"{b.RunName} · {b.Files.Count} {(b.Files.Count == 1 ? "file" : "files")}"; } }
}

// Separate tables keep recipient-unaware handoff v1 clients from consuming Scout work.
public sealed class RoutedHandoffs
{
    public string Path { get; }
    public RoutedHandoffs(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        using var db = new Sqlite(Path);
        db.Exec("BEGIN IMMEDIATE");
        try
        {
            db.Exec("CREATE TABLE IF NOT EXISTS doggo_migrations(component TEXT NOT NULL,version INTEGER NOT NULL,applied_utc TEXT NOT NULL,PRIMARY KEY(component,version))");
            EnsureVersion(db, "doggo");
            EnsureVersion(db, "handoff_routes");
            db.Exec("CREATE TABLE IF NOT EXISTS handoff_routed_batches(id TEXT PRIMARY KEY,source_app TEXT NOT NULL,target_app TEXT NOT NULL,contract TEXT NOT NULL,version INTEGER NOT NULL,digest TEXT NOT NULL,payload TEXT NOT NULL,status TEXT NOT NULL CHECK(status IN ('Offered','Accepted','Declined')),offered_utc TEXT NOT NULL,response_utc TEXT)");
            db.Exec("CREATE INDEX IF NOT EXISTS handoff_recipient ON handoff_routed_batches(target_app,status)");
            db.Exec("CREATE TABLE IF NOT EXISTS handoff_routed_events(id INTEGER PRIMARY KEY,batch_id TEXT NOT NULL REFERENCES handoff_routed_batches(id),actor TEXT NOT NULL,kind TEXT NOT NULL,created_utc TEXT NOT NULL)");
            db.Exec("COMMIT");
        }
        catch { db.Exec("ROLLBACK"); throw; }
    }
    public static void EnsureVersion(Sqlite db, string component)
    {
        var value = db.Query("SELECT MAX(version) FROM doggo_migrations WHERE component=?", component)[0][0];
        if (int.TryParse(value, out var version) && version > 1) throw new IOException("A newer app is required for " + component + ".");
        db.Exec("INSERT OR IGNORE INTO doggo_migrations VALUES(?,1,?)", component, DateTime.UtcNow.ToString("O"));
    }
    public void Offer(string id, string digest, string payload)
    {
        var batch = ScoutHandoff.Read(id, digest, payload);
        using var db = new Sqlite(Path);
        db.Exec("BEGIN IMMEDIATE");
        try
        {
            var old = db.Query("SELECT digest,payload,source_app,target_app,contract,version FROM handoff_routed_batches WHERE id=?", id);
            if (old.Count > 0 && !old[0].SequenceEqual(new[] { digest, payload, batch.SourceApp, batch.TargetApp, batch.Contract, "1" }))
                throw new IOException("This handoff ID already has different contents.");
            if (old.Count == 0)
            {
                var now = DateTime.UtcNow.ToString("O");
                db.Exec("INSERT INTO handoff_routed_batches VALUES(?,?,?,?,1,?,?,'Offered',?,NULL)", id, batch.SourceApp, batch.TargetApp, batch.Contract, digest, payload, now);
                db.Exec("INSERT INTO handoff_routed_events(batch_id,actor,kind,created_utc) VALUES(?,'tucker','Offered',?)", id, now);
            }
            db.Exec("COMMIT");
        }
        catch { db.Exec("ROLLBACK"); throw; }
    }
    public List<RoutedBatch> Inbox(string recipient)
    {
        using var db = new Sqlite(Path);
        return db.Query("SELECT id,digest,payload,status FROM handoff_routed_batches WHERE target_app=? AND source_app='tucker' AND contract='tucker.scout.outputs' AND version=1 ORDER BY offered_utc,id", recipient)
            .Select(r => new RoutedBatch(r[0], r[1], r[2], r[3])).ToList();
    }
    public void Decline(string id, string recipient)
    {
        using var db = new Sqlite(Path);
        db.Exec("BEGIN IMMEDIATE");
        try
        {
            var row = db.Query("SELECT status FROM handoff_routed_batches WHERE id=? AND target_app=? AND source_app='tucker' AND contract='tucker.scout.outputs' AND version=1", id, recipient);
            if (row.Count == 0) throw new InvalidDataException("Handoff is not addressed to this app.");
            if (row[0][0] == "Accepted") throw new InvalidOperationException("This handoff has already been accepted.");
            if (row[0][0] == "Offered")
            {
                var now = DateTime.UtcNow.ToString("O");
                db.Exec("UPDATE handoff_routed_batches SET status='Declined',response_utc=? WHERE id=?", now, id);
                db.Exec("INSERT INTO handoff_routed_events(batch_id,actor,kind,created_utc) VALUES(?,?,'Declined',?)", id, recipient, now);
            }
            db.Exec("COMMIT");
        }
        catch { db.Exec("ROLLBACK"); throw; }
    }
}
