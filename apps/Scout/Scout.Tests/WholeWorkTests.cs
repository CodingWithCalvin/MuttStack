using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;

static class WholeWorkTests {
 static readonly Episode[] Parts=[new(10,1,1,"The Rescue","The crew rescues the stranded captain."),new(11,1,2,"The Rescue - Part 2","The crew rescues the stranded captain.")];
 static readonly Cue[] Cues=[new(60,"The captain is stranded beyond the canyon."),new(900,"The crew launches their rescue mission."),new(1400,"They reach the captain and clear the debris."),new(2500,"The captain is safe aboard the rescue ship.")];
 static readonly RecordingExtent Extent=new(2700,[1320,1325,1330,1335,1340,1345]);
 const string Endpoint="http://127.0.0.1:1234/v1/chat/completions";
 static void Check(bool value){if(!value)throw new Exception("Whole-work regression");}
 static void Reject(Action action){try{action();throw new Exception("Invalid whole-work proof accepted");}catch(InvalidDataException){}}
 static JsonObject Audit(bool complete=true)=>new(){["candidates"]=new JsonObject{["c0"]=new JsonObject{["role"]="main_story",["statements"]=new JsonObject{
  ["g0"]=new JsonObject{["verdict"]="supported",["evidence"]=new JsonArray("s0","s3")}}}},
  ["story_extent"]=new JsonObject{["opening"]=new JsonArray("s0"),["development"]=new JsonArray("s2"),["ending"]=new JsonArray("s3")},
  ["recording"]=new JsonObject{["kind"]=complete?"continuous_parts":"unresolved",["candidates"]=complete?new JsonArray("c0","c1"):new JsonArray()}};
 static JsonObject Expand(JsonObject raw,RecordingExtent? extent=null,Episode[]? episodes=null) {
  var blocks=DialogueMatcher.Blocks(Cues);var result=WholeWorkGrounding.Expand(raw,DialogueEvidence.Spans(blocks),blocks,episodes??Parts,extent??Extent);
  DialogueMatcher.Validate(result,blocks,episodes??Parts,true,true);return result;
 }
 sealed class Handler(Func<JsonObject,int,JsonObject> reply):HttpMessageHandler {
  public List<JsonObject> Sources=[];
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
   var body=JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;var source=JsonNode.Parse(body["messages"]![1].Text("content"))!.AsObject();Sources.Add(source);
   return new(HttpStatusCode.OK){Content=new StringContent(new JsonObject{["choices"]=new JsonArray(new JsonObject{["finish_reason"]="stop",["message"]=new JsonObject{["content"]=reply(source,Sources.Count).ToJsonString()}})}.ToJsonString())};
  }
 }
 public static void Run(Action<string,Action> test) {
  test("Complete continuous work maps both catalog parts without inventing an edit point",()=>{
   var result=Expand(Audit());Check(result.Array("matches").Select(m=>m.Int("episode_id")).SequenceEqual(new[]{10,11})&&result["installment_evidence"]==null);
   result["method"]=DialogueMatcher.Version;result["dialogue_hash"]=CollectionResolver.Hash(JsonSerializer.Serialize(Cues,Data.Options));
   var row=new JsonObject{["duration"]=2700.0,["reasoning"]=result,["dialogue"]=new JsonObject{["cues"]=JsonSerializer.SerializeToNode(Cues,Data.Options)},
    ["candidates"]=new JsonArray(Parts.Select(e=>(JsonNode)new JsonObject{["episode"]=JsonSerializer.SerializeToNode(e,Data.Options)}).ToArray())};
   Check(MultipartEvidence.Combined(row));row["duration"]=1300;Check(!MultipartEvidence.Combined(row));
  });
  var corrupt=new Action<JsonObject>[] {
   r=>r["story_extent"]!["ending"]=new JsonArray("s2"),r=>r["story_extent"]!["opening"]=new JsonArray("s3"),
   r=>r["story_extent"]!["development"]=new JsonArray("s999"),r=>r["story_extent"]!["ending"]=new JsonArray(),
   r=>r["recording"]!["candidates"]=new JsonArray("c0"),r=>r["recording"]!["candidates"]=new JsonArray("c1","c0"),
   r=>r["candidates"]!["c0"]!["role"]="recap",r=>r["candidates"]!["c0"]!["role"]="preview_or_promo",
   r=>r["candidates"]!["c0"]!["statements"]!["g0"]!["verdict"]="insufficient_evidence",
   r=>r["candidates"]!["c0"]!["statements"]!["g0"]!["verdict"]="contradicted"
  };
  for(int i=0;i<corrupt.Length;i++){var mutation=corrupt[i];test("Complete-work rejects missing, invented or conflicting evidence "+i,()=>{var raw=Audit();mutation(raw);Reject(()=>Expand(raw));});}
  foreach(var extent in new[]{new RecordingExtent(1350,Extent.SingleEpisodeSeconds),new(3300,Extent.SingleEpisodeSeconds),new(2700,[1320,1330,1340,1350]),new(2700,[600,900,1320,2200,3200]),new(double.NaN,Extent.SingleEpisodeSeconds)})
   test("Runtime cannot establish an incomplete work or replace a stable peer sample "+extent.DurationSeconds+"/"+extent.SingleEpisodeSeconds.Length,()=>Reject(()=>Expand(Audit(),extent)));
  test("Runtime cannot turn unrelated stories or a missing part into one complete work",()=>{
   Reject(()=>Expand(Audit(),episodes:[Parts[0],Parts[1] with{Title="Another Story"}]));
   Reject(()=>Expand(Audit(),episodes:[Parts[0],Parts[1] with{Number=3}]));
  });
  test("A model abstention remains unresolved even with double runtime",()=>{
   var result=Expand(Audit(false));Check(result.Array("matches").Count==0&&WholeWorkGrounding.CoverageOnly(result,Parts));
  });
  foreach(bool complete in new[]{true,false})test("Two complete-work title audits avoid a full catalog search: "+complete,()=>{
   using var handler=new Handler((source,_)=>{Check(source["catalog"]==null&&source["measured_extent"]!=null&&source.Text("stage_instructions").Contains("Do not split"));return Audit(complete);});
   using var http=new HttpClient(handler);var matcher=new DialogueMatcher(http,new(){UseEvidenceRetrieval=true});var candidate=new TitleCandidate("wrong-provider.S99E01.The.Rescue.mkv",Parts,"title");
   string before=matcher.CacheScope;
   using(matcher.ForRecording(Extent)) {
    Check(matcher.CacheScope!=before);var result=matcher.VerifyTitleCandidateAsync(Cues,candidate,"m",Endpoint).GetAwaiter().GetResult();
    Check(handler.Sources.Count==2&&TitleCandidates.Accepted(result,candidate,Cues)==complete&&result.Bool("coverage_review_only")==!complete);
   }
   Check(matcher.CacheScope==before);
  });
  test("Independent complete-work coverage disagreement cannot assign episodes",()=>{
   using var handler=new Handler((_,n)=>Audit(n==1));using var http=new HttpClient(handler);var matcher=new DialogueMatcher(http);
   using var scope=matcher.ForRecording(Extent);Reject(()=>matcher.VerifyTitleCandidateAsync(Cues,new("release",Parts,"title"),"m",Endpoint).GetAwaiter().GetResult());Check(handler.Sources.Count==2);
  });
  test("A coverage-only retrieval audit does not restart the entire guide search",()=>{
   using var handler=new Handler((source,n)=>n==1?new JsonObject{["candidates"]=new JsonArray(10,11),["search_more"]=false}:Audit(false));
   using var http=new HttpClient(handler);var matcher=new DialogueMatcher(http,new(){UseEvidenceRetrieval=true});using var scope=matcher.ForRecording(Extent);
   var result=matcher.IdentifyAsync(Cues,Parts,"m",Endpoint).GetAwaiter().GetResult();Check(result.Bool("coverage_review_only")&&handler.Sources.Count==3);
  });
 }
}
