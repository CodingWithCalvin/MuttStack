using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Doggo;
namespace Ripley;

public static class TuckerHandoff
{
 public static string ManifestPath(string state,string runId)=>Path.Combine(state,"tucker-outbox",Guid.Parse(runId).ToString("D")+".json");
 public static string Prepare(string state,JsonObject run) {
  var id=Guid.Parse(run["Id"]!.ToString()).ToString("D");var path=ManifestPath(state,id);
  if(File.Exists(path))return id; // Retry the immutable original offer, never a new copy.
  if(run["QueuedAtUtc"]!=null||run["Status"]?.ToString()!="Completed"||run["Files"] is not JsonArray {Count:>0} files)throw new IOException("Only a completed run that was not queued in Ripley can be sent to Tucker.");
  var entries=new JsonArray();
  foreach(var j in files.OfType<JsonObject>()) {
   var source=j["Source"]!.ToString();var stat=new FileInfo(source);
   if(!Path.IsPathFullyQualified(source)||!stat.Exists||stat.Length!=j["SourceSize"]!.GetValue<long>()||stat.LastWriteTimeUtc!=j["SourceModified"]!.GetValue<DateTime>()||RawRip.IsStagingFile(source))throw new IOException("Raw file changed or is missing: "+source);
   var metadata=j["DiscMetadata"]?.ToString();
   var disc=!string.IsNullOrWhiteSpace(metadata)&&File.Exists(metadata)?JsonNode.Parse(File.ReadAllText(metadata))!.AsObject():null;
   if(disc!=null&&disc["status"]?.ToString()!="Ripped")throw new IOException("Raw publication is incomplete: "+source);
   JsonNode? Value(string key,string fallback)=>disc?[key]?.DeepClone()??run[fallback]?.DeepClone();
   entries.Add(new JsonObject { ["Source"]=Path.GetFullPath(source),["SourceSize"]=stat.Length,["SourceModified"]=stat.LastWriteTimeUtc,
    ["Series"]=disc?["series_identity"]?.DeepClone()??j["Series"]?.DeepClone()??run["Series"]?.DeepClone(),
    ["Season"]=Value("season","Season"),["VolumeNumber"]=Value("volume_number","Volume"),["DiscNumber"]=Value("disc_number","DiscNumber"),["DiscLabel"]=Value("disc_label","DiscLabel"),["DiscId"]=id,
    ["RipDate"]=disc?["rip_date"]?.DeepClone()??disc?["completed_utc"]?.DeepClone()??run["Created"]?.DeepClone() });
  }
  var manifest=new JsonObject{["Version"]=1,["Id"]=id,["SourceApp"]="ripley",["TargetApp"]="tucker",["Files"]=entries};
  QueueStore.Write(path,manifest);return id;
 }
 public static void Deliver(string state,string id,string database) {
  var bytes=File.ReadAllBytes(ManifestPath(state,id));var payload=Encoding.UTF8.GetString(bytes);
  new DoggoInbox(database).Offer(Guid.Parse(id).ToString("D"),Convert.ToHexString(SHA256.HashData(bytes)),payload);
 }
}
