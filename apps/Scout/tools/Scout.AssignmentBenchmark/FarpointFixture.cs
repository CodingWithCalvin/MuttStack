using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;

static class FarpointFixture {
 public static void Prepare(string snapshot,string output,string statusDatabase) {
  if(File.Exists(output))throw new IOException("Fixture already exists; it will not be replaced.");
  if(!Path.IsPathFullyQualified(statusDatabase)||!File.Exists(statusDatabase))throw new ArgumentException("Provide the actual live Scout database path for read-only idle checks; do not infer it from a sandbox's LocalApplicationData.");
  using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.GetFullPath(snapshot),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());db.Open();
  using var cmd=db.CreateCommand();cmd.CommandText="SELECT document,payload FROM file_results WHERE source LIKE '%Encounter.at.Farpoint.(1).mkv'";
  string report;JsonObject row;using(var r=cmd.ExecuteReader()){if(!r.Read())throw new InvalidDataException("Saved Farpoint dialogue missing.");report=r.GetString(0);row=JsonNode.Parse(r.GetString(1))!.AsObject();if(r.Read())throw new InvalidDataException("Ambiguous saved Farpoint fixture.");}
  var cues=row["dialogue"]!.Array("cues").Deserialize<List<Cue>>(Data.Options)!;
  cmd.CommandText="SELECT content FROM documents WHERE path=$p";cmd.Parameters.AddWithValue("$p",Path.Combine(Path.GetDirectoryName(report)!,"catalog.json"));
  var catalog=JsonNode.Parse((byte[])cmd.ExecuteScalar()!)!;
  cmd.CommandText="SELECT content FROM documents WHERE kind='settings.json'";cmd.Parameters.Clear();
  var settings=JsonSerializer.Deserialize<AppSettings>((byte[])cmd.ExecuteScalar()!,Data.Options)!;
  settings.Provider="managed";settings.GpuLayers=0;settings.ThinkingEnabled=false;settings.ContextSize=24576;settings.MaxOutputTokens=4096;settings.RequestTimeoutSeconds=7200;settings.MaxRequestsPerRun=20;settings.AutoDownloadMissing=false;settings.AllowDownloads=false;
  if(settings.DownloadProfile!="gemma4-31b")throw new InvalidDataException("The saved setup is not the Gemma profile.");
  var hash=CollectionResolver.Hash(JsonSerializer.Serialize(cues,Data.Options));
  var fixture=new JsonObject{["title"]="Farpoint · real-model saved-dialogue test",["mode"]="identify",["inference_deadline_minutes"]=240,
   ["idle_status_database"]=statusDatabase,
   ["source_snapshot"]=Path.GetFullPath(snapshot),["settings"]=JsonSerializer.SerializeToNode(settings,Data.Options),["catalog"]=catalog,
   ["cases"]=new JsonArray(new JsonObject{["case_id"]=hash[..12],["source"]=row["path"]!.DeepClone(),["cues"]=JsonSerializer.SerializeToNode(cues,Data.Options),["dialogue_hash"]=hash}),
   ["limitations"]="One saved-dialogue development case. No independent ground-truth label is supplied. This tests real-model context recovery at 24576 tokens, not general episode-identification accuracy."};
  Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);File.WriteAllText(output,fixture.ToJsonString(Data.Options));
  Console.WriteLine($"Frozen {cues.Count} original cues and {catalog.Array("episodes").Count} guide entries. CPU only; thinking disabled; no expected assignment supplied.");
 }
}
