using System.Text.Json.Nodes;
namespace Doggo;

// Shared protocol only: neither consumer nor producer private queues are opened here.
public sealed class DoggoInbox
{
 public string Path {get;}
 public static string DefaultPath=>System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Doggo","doggo.db");
 public DoggoInbox(string path) {
  Path=System.IO.Path.GetFullPath(path);Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
  using var db=new Sqlite(Path);db.Exec("PRAGMA journal_mode=WAL");db.Exec("BEGIN IMMEDIATE");
  try {
   db.Exec("CREATE TABLE IF NOT EXISTS doggo_migrations(component TEXT NOT NULL,version INTEGER NOT NULL,applied_utc TEXT NOT NULL,PRIMARY KEY(component,version))");
   foreach(var component in new[]{"doggo","handoff"}) {
    var row=db.Query("SELECT MAX(version) FROM doggo_migrations WHERE component=?",component);
    if(int.TryParse(row[0][0],out var v)&&v>1)throw new IOException("The shared "+component+" schema needs a newer app.");
   }
   db.Exec("CREATE TABLE IF NOT EXISTS handoff_batches(id TEXT PRIMARY KEY,digest TEXT NOT NULL,payload TEXT NOT NULL,status TEXT NOT NULL CHECK(status IN ('Offered','Accepted','Declined')),offered_utc TEXT NOT NULL,accepted_utc TEXT)");
   db.Exec("CREATE TABLE IF NOT EXISTS handoff_events(id INTEGER PRIMARY KEY,batch_id TEXT NOT NULL REFERENCES handoff_batches(id),kind TEXT NOT NULL,created_utc TEXT NOT NULL)");
   foreach(var component in new[]{"doggo","handoff"})db.Exec("INSERT OR IGNORE INTO doggo_migrations VALUES(?,1,?)",component,DateTime.UtcNow.ToString("O"));
   db.Exec("COMMIT");
  }catch{db.Exec("ROLLBACK");throw;}
 }
 public void Offer(string id,string digest,string payload) {
  var manifest=JsonNode.Parse(payload)!;
  if(!Guid.TryParse(id,out _)||manifest["Id"]?.ToString()!=id||manifest["TargetApp"]?.ToString()!="tucker"||manifest["Version"]?.GetValue<int>()!=1||manifest["Files"] is not JsonArray {Count:>0})throw new IOException("Invalid Tucker handoff.");
  using var db=new Sqlite(Path);db.Exec("BEGIN IMMEDIATE");
  try {
   var old=db.Query("SELECT digest FROM handoff_batches WHERE id=?",id);
   if(old.Count>0&&old[0][0]!=digest)throw new IOException("This handoff ID already has different contents.");
   if(old.Count==0){var now=DateTime.UtcNow.ToString("O");db.Exec("INSERT INTO handoff_batches VALUES(?,?,?,'Offered',?,NULL)",id,digest,payload,now);db.Exec("INSERT INTO handoff_events(batch_id,kind,created_utc) VALUES(?,'Offered',?)",id,now);}
   db.Exec("COMMIT");
  }catch{db.Exec("ROLLBACK");throw;}
 }
 public string Status(string id){using var db=new Sqlite(Path);return db.Query("SELECT status FROM handoff_batches WHERE id=?",id).FirstOrDefault()?[0]??"Not delivered";}
 public bool CompletedSource(string key){using var db=new Sqlite(Path);if(db.Query("SELECT name FROM sqlite_master WHERE type='table' AND name='ripley_completed_sources'").Count==0)return false;return db.Query("SELECT 1 FROM ripley_completed_sources WHERE source_key=?",key).Count>0;}
 public static string SourceKey(string source,long size,DateTime modified)=>System.IO.Path.GetFullPath(source).ToUpperInvariant()+"|"+size+"|"+modified.Ticks;
}
