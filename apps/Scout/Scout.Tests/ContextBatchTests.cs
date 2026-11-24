using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;
static class ContextBatchTests {
 static void Check(bool value){if(!value)throw new Exception("Context recovery assertion failed.");}
 public static void Run(Action<string,Action> test) {
  var cues=Enumerable.Range(0,32).Select(i=>new Cue(i*5,"Original dialogue words "+i)).ToArray();
  var episodes=Enumerable.Range(1,12).Select(i=>new Episode(i,1,i,"Episode "+i,"Guide scenario "+i)).ToArray();
  foreach(bool disagree in new[]{false,true})test(disagree?"Catalog batch disagreement holds the assignment":"Catalog overflow searches every guide entry retains all dialogue and independently verifies suggestions",()=>{
   var handler=new Handler(episodes.Length,disagree);using var http=new HttpClient(handler);
   try {
    var result=new DialogueMatcher(http).IdentifyAsync(cues,episodes,"test","http://127.0.0.1:1/v1/chat/completions").GetAwaiter().GetResult();
    Check(!disagree&&result.Array("matches")[0].Int("episode_id")==1&&result["context_recovery"].Int("catalog_count")==12);
   }catch(InvalidDataException ex){Check(disagree&&ex.Message.Contains("disagree"));}
   var sources=handler.Requests.Select(r=>JsonNode.Parse(r["messages"]![1].Text("content"))!).ToArray();
   Check(sources.All(s=>JsonNode.DeepEquals(s["dialogue_blocks"],sources[0]["dialogue_blocks"])));
   Check(handler.Scanned.Order().SequenceEqual(episodes.Select(e=>e.Id))&&handler.Verifications==2);
   Check(sources.Skip(2).All(s=>!s.ToJsonString().Contains("GENERATED BATCH SUMMARY")));
  });
  test("A single guide context overflow remains held with an output-inclusive sizing recommendation",()=>{
   var handler=new Handler(1,false);using var http=new HttpClient(handler);
   try{new DialogueMatcher(http).DirectAsync(cues,[episodes[0]],"test","http://127.0.0.1:1/v1/chat/completions",false).GetAwaiter().GetResult();throw new Exception("Overflow accepted");}
   catch(ModelContextException ex){Check(ex.Message.Contains("32,768")&&handler.Requests.Count==2);}
  });
  test("Unrelated model failures do not trigger catalog partition inference",()=>{
   using var http=new HttpClient(new TestHandler(_=>new(HttpStatusCode.Unauthorized){Content=new StringContent("{}") }));
   try{new DialogueMatcher(http).IdentifyAsync(cues,episodes,"test","http://127.0.0.1:1/v1/chat/completions").GetAwaiter().GetResult();throw new Exception("Failure accepted");}catch(HttpRequestException ex){Check(ex.StatusCode==HttpStatusCode.Unauthorized);}
  });
 }
 internal sealed class Handler(int overflowAt,bool disagree):HttpMessageHandler {
  public List<JsonObject> Requests=[];public List<int> Scanned=[];public int Verifications;
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
   var payload=JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();Requests.Add(payload);var source=JsonNode.Parse(payload["messages"]![1].Text("content"))!;var catalog=source.Array("catalog");
   if(catalog.Count>=overflowAt)return new(HttpStatusCode.BadRequest){Content=new StringContent("""{"error":{"type":"exceed_context_size_error","n_prompt_tokens":26472,"n_ctx":24576}}""")};
   bool verify=payload["response_format"]!["json_schema"]!["schema"]!["properties"]?["checks"]!=null;
   var chosen=catalog[0]!;
   JsonObject result;
   if(verify) {
    Verifications++;if(disagree&&Verifications==2)chosen=catalog.Last()!;
    result=new(){["assessment"]="supported",["coverage"]="complete",["checks"]=new JsonArray(new JsonObject{["episode_id"]=chosen.Int("id"),["verdict"]="supported",["content_type"]="complete_story",["supporting_blocks"]=new JsonArray(0,1),["contradicting_blocks"]=new JsonArray(),["reason"]="Defining events match the original dialogue."}),["note"]=""};
   } else {
    Scanned.AddRange(catalog.Select(e=>e.Int("id")));
    result=new(){["assessment"]="supported",["coverage"]="complete",["summary"]="GENERATED BATCH SUMMARY",["matches"]=new JsonArray(new JsonObject{["episode_title"]=chosen.Text("title"),["reason"]="The source matches the guide events."}),["candidate_checks"]=new JsonArray(new JsonObject{["episode_title"]=chosen.Text("title"),["content_type"]="complete_story",["supporting_blocks"]=new JsonArray(0,1),["contradicting_blocks"]=new JsonArray(),["event_comparison"]="The original events agree."}),["alternatives"]=new JsonArray()};
   }
   return new(HttpStatusCode.OK){Content=new StringContent(new JsonObject{["choices"]=new JsonArray(new JsonObject{["finish_reason"]="stop",["message"]=new JsonObject{["content"]=result.ToJsonString()}})}.ToJsonString())};
  }
 }
}
