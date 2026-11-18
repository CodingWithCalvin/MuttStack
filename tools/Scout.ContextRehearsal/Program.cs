using Microsoft.Data.Sqlite;
using Scout.Core;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
// Replays context routing with saved evidence and a deterministic in-memory HTTP
// service. This checks request construction/coverage, NOT episode accuracy or fit
// against a real tokenizer. It never contacts or starts a model server.
if(args.Length!=3){Console.Error.WriteLine("Usage: Scout.ContextRehearsal <isolated-scout-snapshot.db> <source-filename-fragment> <output.json>");return 2;}
using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.GetFullPath(args[0]),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());db.Open();
using var cmd=db.CreateCommand();cmd.CommandText="SELECT document,payload FROM file_results WHERE source LIKE $name ORDER BY document LIMIT 1";cmd.Parameters.AddWithValue("$name","%"+args[1]+"%");
string report;JsonObject row;using(var r=cmd.ExecuteReader()){if(!r.Read())throw new InvalidDataException("Saved file not found.");report=r.GetString(0);row=JsonNode.Parse(r.GetString(1))!.AsObject();}
cmd.CommandText="SELECT content FROM documents WHERE path=$path";cmd.Parameters.Clear();cmd.Parameters.AddWithValue("$path",Path.Combine(Path.GetDirectoryName(report)!,"catalog.json"));
var episodes=JsonNode.Parse((byte[])cmd.ExecuteScalar()!)!.Array("episodes").Select(e=>Data.Episode(e!)).ToArray();
var cues=row["dialogue"]!.Array("cues").Deserialize<Cue[]>(Data.Options)!;
var handler=new RoutingOnlyHandler(episodes.Length);using var http=new HttpClient(handler);
var outcome=await new DialogueMatcher(http).IdentifyAsync(cues,episodes,"offline-routing-only","http://127.0.0.1:1/v1/chat/completions");
var allDialogue=handler.Sources.All(s=>JsonNode.DeepEquals(s["dialogue_blocks"],handler.Sources[0]["dialogue_blocks"]));
var allGuide=handler.Scanned.Order().SequenceEqual(episodes.Select(e=>e.Id).Order());
if(!allDialogue||!allGuide||outcome.Array("matches").Count>0)throw new InvalidDataException("Routing rehearsal failed.");
var result=new {mode="offline_transport_rehearsal_no_inference",source=Path.GetFileName(row.Text("path")),cues=cues.Length,catalog_entries=episodes.Length,
 all_original_dialogue_preserved=allDialogue,all_guide_entries_searched=allGuide,requests=handler.Sizes,verification_requests=handler.Verifications,
 accepted_assignments=0,live_model_contacted=false,scout_results_changed=false,limitations="HTTP context rejection is simulated. Character sizes are not token counts; real runtime fit and model quality require an idle-model test."};
File.WriteAllText(args[2],JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(File.ReadAllText(args[2]));return 0;

sealed class RoutingOnlyHandler(int fullCount):HttpMessageHandler {
 public List<JsonNode> Sources=[];public List<object> Sizes=[];public List<int> Scanned=[];public int Verifications;
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
  var body=JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;var text=body["messages"]![1].Text("content");var source=JsonNode.Parse(text)!;Sources.Add(source);
  var catalog=source.Array("catalog");bool verify=body["response_format"]!["json_schema"]!["schema"]!["properties"]?["checks"]!=null;
  Sizes.Add(new{guide_entries=catalog.Count,characters=text.Length+body["messages"]![0].Text("content").Length,stage=verify?"verification":"search",simulated_overflow=catalog.Count==fullCount});
  if(catalog.Count==fullCount)return new(HttpStatusCode.BadRequest){Content=new StringContent("""{"error":{"type":"exceed_context_size_error","n_prompt_tokens":26472,"n_ctx":24576}}""")};
  JsonObject response;
  if(verify){Verifications++;response=new(){["assessment"]="uncertain",["coverage"]="unknown",["checks"]=new JsonArray(),["note"]="Offline routing rehearsal; no model decision."};}
  else {
   Scanned.AddRange(catalog.Select(e=>e.Int("id")));response=new(){["assessment"]="uncertain",["coverage"]="unknown",["summary"]="Offline routing rehearsal; no model decision.",["matches"]=new JsonArray(),["alternatives"]=new JsonArray(),
    ["candidate_checks"]=new JsonArray(new JsonObject{["episode_title"]=catalog[0].Text("title"),["content_type"]="unknown",["supporting_blocks"]=new JsonArray(),["contradicting_blocks"]=new JsonArray(),["event_comparison"]="Candidate placeholder for request-routing validation only."})};
  }
  return new(HttpStatusCode.OK){Content=new StringContent(new JsonObject{["choices"]=new JsonArray(new JsonObject{["finish_reason"]="stop",["message"]=new JsonObject{["content"]=response.ToJsonString()}})}.ToJsonString())};
 }
}
