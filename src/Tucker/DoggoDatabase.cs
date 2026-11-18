using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace Tucker;

using Doggo;

public record InboxItem(string Id,string Status,string Summary,string Digest,string Payload);
public sealed class DoggoDatabase
{
    public string Path {get;}
    public DoggoDatabase(string path) {
        Path=System.IO.Path.GetFullPath(path);Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        _=new DoggoInbox(Path);
        using var db=Open();db.Exec("BEGIN IMMEDIATE");
        try {
            db.Exec("CREATE TABLE IF NOT EXISTS doggo_migrations(component TEXT NOT NULL,version INTEGER NOT NULL,applied_utc TEXT NOT NULL,PRIMARY KEY(component,version))");
            foreach(var component in new[]{"doggo","handoff","tucker"}) {
                var versions=db.Query("SELECT MAX(version) FROM doggo_migrations WHERE component=?",component);
                if(int.TryParse(versions[0][0],out var version)&&version>(component=="tucker"?2:1))throw new IOException("This "+component+" schema needs a newer Tucker. Existing data was kept.");
            }
            db.Exec("CREATE TABLE IF NOT EXISTS handoff_batches(id TEXT PRIMARY KEY,digest TEXT NOT NULL,payload TEXT NOT NULL,status TEXT NOT NULL CHECK(status IN ('Offered','Accepted','Declined')),offered_utc TEXT NOT NULL,accepted_utc TEXT)");
            db.Exec("CREATE TABLE IF NOT EXISTS handoff_events(id INTEGER PRIMARY KEY,batch_id TEXT NOT NULL REFERENCES handoff_batches(id),kind TEXT NOT NULL,created_utc TEXT NOT NULL)");
            db.Exec("CREATE TABLE IF NOT EXISTS tucker_jobs(id TEXT PRIMARY KEY,ordinal INTEGER NOT NULL,queue_id TEXT NOT NULL,payload TEXT NOT NULL)");
            db.Exec("CREATE TABLE IF NOT EXISTS tucker_imported_sources(source_key TEXT PRIMARY KEY)");
            db.Exec("CREATE TABLE IF NOT EXISTS tucker_receipts(id TEXT PRIMARY KEY,digest TEXT NOT NULL,saved_utc TEXT NOT NULL,files INTEGER NOT NULL)");
            db.Exec("CREATE TABLE IF NOT EXISTS tucker_presets(path TEXT PRIMARY KEY,json TEXT NOT NULL)");
            db.Exec("CREATE TABLE IF NOT EXISTS tucker_settings(id TEXT PRIMARY KEY,json TEXT NOT NULL)");
            db.Exec("CREATE TABLE IF NOT EXISTS tucker_archived_runs(run_id TEXT PRIMARY KEY)");
            foreach(var component in new[]{"doggo","handoff","tucker"})db.Exec("INSERT OR IGNORE INTO doggo_migrations VALUES(?,1,?)",component,DateTime.UtcNow.ToString("O"));
            db.Exec("INSERT OR IGNORE INTO doggo_migrations VALUES('tucker',2,?)",DateTime.UtcNow.ToString("O"));
            db.Exec("COMMIT");
        } catch {db.Exec("ROLLBACK");throw;}
    }
    Sqlite Open()=>new(Path);
    public QueueState Read() {
        using var db=Open();var state=new QueueState();
        foreach(var row in db.Query("SELECT payload FROM tucker_jobs ORDER BY ordinal"))state.Jobs.Add(JsonSerializer.Deserialize<Job>(row[0])!);
        foreach(var row in db.Query("SELECT source_key FROM tucker_imported_sources"))state.ImportedSources.Add(row[0]);
        foreach(var row in db.Query("SELECT run_id FROM tucker_archived_runs"))state.ArchivedRuns.Add(row[0]);
        foreach(var row in db.Query("SELECT id,digest,saved_utc,files FROM tucker_receipts"))state.Receipts.Add(new(row[0],row[1],DateTime.Parse(row[2],null,System.Globalization.DateTimeStyles.RoundtripKind),int.Parse(row[3])));
        return state;
    }
    public void Save(QueueState state) {
        using var db=Open();db.Exec("BEGIN IMMEDIATE");
        try {
            db.Exec("DELETE FROM tucker_jobs");
            for(int i=0;i<state.Jobs.Count;i++){var j=state.Jobs[i];db.Exec("INSERT INTO tucker_jobs VALUES(?,?,?,?)",j.Id,i.ToString(),j.QueueId,JsonSerializer.Serialize(j));}
            db.Exec("DELETE FROM tucker_archived_runs");
            foreach(var id in state.ArchivedRuns.Where(id=>state.Jobs.Any(j=>j.QueueId==id)))db.Exec("INSERT INTO tucker_archived_runs VALUES(?)",id);
            foreach(var key in state.ImportedSources)db.Exec("INSERT OR IGNORE INTO tucker_imported_sources VALUES(?)",key);
            foreach(var r in state.Receipts) {
                var offered=db.Query("SELECT digest,status FROM handoff_batches WHERE id=?",r.Id);
                var saved=db.Query("SELECT digest FROM tucker_receipts WHERE id=?",r.Id);
                if(offered.Count==0||offered[0][0]!=r.Digest||offered[0][1]=="Declined"||saved.Count>0&&saved[0][0]!=r.Digest)throw new IOException("Handoff changed or was declined. Refresh the inbox.");
                db.Exec("INSERT OR IGNORE INTO tucker_receipts VALUES(?,?,?,?)",r.Id,r.Digest,r.SavedUtc.ToString("O"),r.Files.ToString());
                if(offered[0][1]=="Offered") {
                    db.Exec("UPDATE handoff_batches SET status='Accepted',accepted_utc=? WHERE id=?",r.SavedUtc.ToString("O"),r.Id);
                    db.Exec("INSERT INTO handoff_events(batch_id,kind,created_utc) VALUES(?,'Accepted',?)",r.Id,r.SavedUtc.ToString("O"));
                }
            }
            db.Exec("COMMIT");
        } catch { db.Exec("ROLLBACK");throw; }
    }
    public void Offer(Handoff handoff) => new DoggoInbox(Path).Offer(handoff.Manifest.Id,handoff.Digest,JsonSerializer.Serialize(handoff.Manifest));
    public bool CompletedInRipley(string key) => new DoggoInbox(Path).CompletedSource(key);
    public int PendingInboxCount(){using var db=Open();return int.Parse(db.Query("SELECT COUNT(*) FROM handoff_batches WHERE status='Offered'")[0][0]);}
    public List<InboxItem> Inbox(){using var db=Open();return db.Query("SELECT id,status,digest,payload FROM handoff_batches ORDER BY offered_utc DESC").Select(r=>{var m=JsonSerializer.Deserialize<HandoffManifest>(r[3])!;return new InboxItem(r[0],r[1],$"{m.Files.Count} files · {m.Files.FirstOrDefault()?.DiscLabel}",r[2],r[3]);}).ToList();}
    public void Decline(string id){using var db=Open();db.Exec("UPDATE handoff_batches SET status='Declined' WHERE id=? AND status='Offered'",id);}
    public T? Settings<T>(){using var db=Open();var r=db.Query("SELECT json FROM tucker_settings WHERE id='preferences'");return r.Count==0?default:JsonSerializer.Deserialize<T>(r[0][0]);}
    public void Settings<T>(T value){using var db=Open();db.Exec("INSERT INTO tucker_settings VALUES('preferences',?) ON CONFLICT(id) DO UPDATE SET json=excluded.json",JsonSerializer.Serialize(value));}
    public void Freeze(Options options){using var db=Open();db.Exec("INSERT OR IGNORE INTO tucker_presets VALUES(?,?)",options.Json,File.ReadAllText(options.Json));}
    public void Materialize(Options options){using var db=Open();var r=db.Query("SELECT json FROM tucker_presets WHERE path=?",options.Json);if(r.Count==0)throw new IOException("Frozen preset is missing from Tucker's database.");Directory.CreateDirectory(System.IO.Path.GetDirectoryName(options.Json)!);File.WriteAllText(options.Json,r[0][0]);}
}
