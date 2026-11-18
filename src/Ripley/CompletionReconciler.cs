using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Doggo;
namespace Ripley;
public sealed record CompletionEvidence(string SourceKey,string RunId,string Source,string Output,string Sidecar,string SidecarSha256,long OutputBytes,DateTime OutputModifiedUtc);
public sealed record Reconciliation(string HistorySha256,int Runs,int Files,List<CompletionEvidence> Completed,List<string> Issues);
public static class CompletionReconciler
{
 public static Reconciliation Inspect(string historyPath,string outputRoot) {
  var bytes=File.ReadAllBytes(historyPath);var history=JsonNode.Parse(bytes)!.AsObject();
  var sessions=history["Sessions"]!.AsArray().OfType<JsonObject>().Where(s=>s["Status"]?.ToString()=="Completed").ToList();
  var candidates=new Dictionary<string,List<(string Path,JsonObject Data)>>(StringComparer.OrdinalIgnoreCase);
  foreach(var sidecar in Directory.EnumerateFiles(outputRoot,"*.ripley.json",new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=FileAttributes.ReparsePoint})) {
   var doc=JsonNode.Parse(File.ReadAllText(sidecar))!.AsObject();var source=doc["source"]?.ToString();if(string.IsNullOrEmpty(source))continue;
   var key=Path.GetFullPath(source);if(!candidates.TryGetValue(key,out var matches))candidates[key]=matches=new();matches.Add((sidecar,doc));
  }
  var completed=new List<CompletionEvidence>();var issues=new List<string>();int count=0;
  foreach(var run in sessions)foreach(var j in run["Files"]!.AsArray().OfType<JsonObject>()) {
   count++;var source=Path.GetFullPath(j["Source"]!.ToString());
   try {
    var stat=new FileInfo(source);var size=j["SourceSize"]!.GetValue<long>();var modified=j["SourceModified"]!.GetValue<DateTime>();
    if(!stat.Exists||stat.Length!=size||stat.LastWriteTimeUtc!=modified)throw new IOException("Raw source missing or changed");
    if(!candidates.TryGetValue(source,out var matches)||matches.Count!=1)throw new IOException("Expected one unambiguous completion sidecar");
    var (sidecar,data)=matches[0];var output=Path.GetFullPath(data["output"]!.ToString());
    if(!output.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot))+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!string.Equals(sidecar,output+".ripley.json",StringComparison.OrdinalIgnoreCase)||data["version"]?.GetValue<int>()!=2)throw new IOException("Completion sidecar does not identify its published output");
    if(!string.Equals(data["disc_metadata"]?.ToString(),j["DiscMetadata"]?.ToString(),StringComparison.OrdinalIgnoreCase))throw new IOException("Disc metadata does not match the recorded rip");
    var video=new FileInfo(output);if(!video.Exists||video.Length<16)throw new IOException("Compressed output missing or empty");
    using(var stream=new FileStream(output,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)){var header=new byte[12];stream.ReadExactly(header);var mkv=header[0]==0x1a&&header[1]==0x45&&header[2]==0xdf&&header[3]==0xa3;var mp4=System.Text.Encoding.ASCII.GetString(header,4,4)=="ftyp";if(!mkv&&!mp4)throw new IOException("Output has no recognized Matroska/MP4 header");}
    completed.Add(new(DoggoInbox.SourceKey(source,size,modified),run["Id"]!.ToString(),source,output,sidecar,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sidecar))),video.Length,video.LastWriteTimeUtc));
   }catch(Exception ex){issues.Add(source+": "+ex.Message);}
  }
  return new(Convert.ToHexString(SHA256.HashData(bytes)),sessions.Count,count,completed,issues);
 }
 public static void Apply(string historyPath,string database,Reconciliation result,string expectedHash) {
  if(result.Issues.Count>0||result.Completed.Count!=result.Files||result.HistorySha256!=expectedHash)throw new IOException("Reconciliation is incomplete or the backed-up history changed.");
  var bytes=File.ReadAllBytes(historyPath);if(Convert.ToHexString(SHA256.HashData(bytes))!=expectedHash)throw new IOException("History changed during reconciliation.");
  var doc=JsonNode.Parse(bytes)!.AsObject();var now=DateTime.UtcNow;
  var inbox=new DoggoInbox(database);using var db=new Sqlite(database);db.Exec("BEGIN IMMEDIATE");
  try {
   var version=db.Query("SELECT MAX(version) FROM doggo_migrations WHERE component='ripley'");if(int.TryParse(version[0][0],out var v)&&v>1)throw new IOException("Newer Ripley schema.");
   db.Exec("CREATE TABLE IF NOT EXISTS ripley_completed_sources(source_key TEXT PRIMARY KEY,run_id TEXT NOT NULL,payload TEXT NOT NULL)");
   db.Exec("CREATE TABLE IF NOT EXISTS ripley_reconciliations(history_sha256 TEXT PRIMARY KEY,saved_utc TEXT NOT NULL,payload TEXT NOT NULL)");
   foreach(var item in result.Completed)db.Exec("INSERT INTO ripley_completed_sources VALUES(?,?,?) ON CONFLICT(source_key) DO UPDATE SET payload=excluded.payload",item.SourceKey,item.RunId,JsonSerializer.Serialize(item));
   db.Exec("INSERT OR IGNORE INTO ripley_reconciliations VALUES(?,?,?)",expectedHash,now.ToString("O"),JsonSerializer.Serialize(result));
   db.Exec("INSERT OR IGNORE INTO doggo_migrations VALUES('ripley',1,?)",now.ToString("O"));db.Exec("COMMIT");
  }catch{db.Exec("ROLLBACK");throw;}
  // The shared completion ledger is authoritative for deduplication. This history annotation is
  // idempotent; if interrupted after the DB commit it is safe to rerun from the unchanged history.
  foreach(var run in doc["Sessions"]!.AsArray().OfType<JsonObject>().Where(s=>s["Status"]?.ToString()=="Completed")) {
   run["CompressionOutcome"]="Completed";run["ReconciledAtUtc"]=now;run["Detail"]="Compression completed · published outputs reconciled. Raw sources retained; run locked.";
   foreach(var job in run["Files"]!.AsArray().OfType<JsonObject>()) {
    var evidence=result.Completed.Single(c=>c.RunId==run["Id"]!.ToString()&&c.Source.Equals(job["Source"]!.ToString(),StringComparison.OrdinalIgnoreCase));
    job["Status"]="Completed";job["CompletedOutput"]=evidence.Output;
   }
  }
  var temp=historyPath+".reconcile.tmp";using(var stream=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(stream,doc,new JsonSerializerOptions{WriteIndented=true});stream.Flush(true);}File.Move(temp,historyPath,true);
 }
}
