using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;
using Scout.Core.Subtitles;

static class TitleCandidateTests {
 static readonly Episode[] Guide=[new(101,3,11,"Silver Comet","The crew rescues the captain. The crew repairs the shuttle."),new(102,3,12,"Golden River","The team returns to the station. The team celebrates their arrival.")];
 static readonly Cue[] Cues=[new(0,"The crew rescues the captain from the cave."),new(40,"The crew repairs the shuttle and prepares to leave."),new(80,"The team returns to the station with everyone aboard."),new(120,"The team celebrates their arrival with a big party.")];
 const string Endpoint="http://127.0.0.1:1234/v1/chat/completions";
 static void Check(bool ok,string reason="Title verification regression"){if(!ok)throw new Exception(reason);}
 static JsonNode Source(JsonObject request)=>JsonNode.Parse(request["messages"]![1].Text("content"))!;
 static JsonObject Audit(string kind="single_episode",int parts=1,int offset=0) {
  var candidates=new JsonObject();
  for(int c=0;c<parts;c++)candidates["c"+c]=new JsonObject{["role"]="main_story",["statements"]=new JsonObject{
   ["g0"]=new JsonObject{["verdict"]="supported",["evidence"]=new JsonArray("s"+(offset+c*2))},
   ["g1"]=new JsonObject{["verdict"]="supported",["evidence"]=new JsonArray("s"+(offset+c*2+1))}}};
  return new(){["candidates"]=candidates,["recording"]=new JsonObject{["kind"]=kind,["candidates"]=new JsonArray(Enumerable.Range(0,parts).Select(i=>(JsonNode?)JsonValue.Create("c"+i)).ToArray())}};
 }
 static JsonObject Unsupported(int parts=1) {
  var r=Audit("unresolved",parts);r["recording"]!["candidates"]=new JsonArray();
  foreach(var candidate in r["candidates"]!.AsObject()) {
   candidate.Value!["role"]="unknown";
   foreach(var fact in candidate.Value["statements"]!.AsObject()){fact.Value!["verdict"]="insufficient_evidence";fact.Value["evidence"]=new JsonArray();}
  }
  return r;
 }
 sealed class Handler(Func<JsonObject,int,JsonObject> reply):HttpMessageHandler {
  public List<JsonObject> Requests=[];
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
   var body=JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();Requests.Add(body);
   var response=reply(body,Requests.Count);return new(HttpStatusCode.OK){Content=new StringContent(new JsonObject{["choices"]=new JsonArray(new JsonObject{["finish_reason"]="stop",["message"]=new JsonObject{["content"]=response.ToJsonString()}})}.ToJsonString())};
  }
 }
 sealed class Progress(Action<string>? callback=null):IProgress<string>{public List<string> Lines=[];public void Report(string value){Lines.Add(value);callback?.Invoke(value);}}
 sealed class EmptyReferences:ISubtitleReferenceSource {public Task<SubtitleReferenceSet> LoadAsync(SeriesIdentity series,IReadOnlyList<Episode> catalog,IProgress<string> progress,CancellationToken ct)=>Task.FromResult(new SubtitleReferenceSet(series,[]));}
 sealed class Fixture:IDisposable {
  public readonly string Output;public readonly List<JsonObject> Rows=[];public readonly Handler Handler;readonly HttpClient http;
  public readonly MediaPipeline Pipeline;public readonly MatchSettings Settings;public int Starts;public int Reads;
  public Fixture(string temp,string name,string[] names,Func<JsonObject,int,JsonObject> reply,bool subtitles=true) {
   var root=Path.Combine(temp,"titles-"+name);var media=Path.Combine(root,"media");Directory.CreateDirectory(media);Output=Path.Combine(root,"output");
   // Store all generated reports/cache entries in an isolated database.
   ScoutStorage.Register(Output,new ScoutDatabase(Path.Combine(root,"test.db")));
   for(int i=0;i<names.Length;i++) {
    var path=Path.Combine(media,names[i]);File.WriteAllText(path,"Synthetic fixture "+i);var size=new FileInfo(path).Length;var mtime=PreviewPlanner.MtimeNs(path);var sample=PreviewPlanner.SampledDigest(path);
    Rows.Add(new(){["path"]=path,["duration"]=160.0,["full_dialogue"]=true,["size"]=size,["source_mtime_ns"]=mtime,["sampled_digest"]=sample,
     ["dialogue"]=new JsonObject{["cues"]=JsonSerializer.SerializeToNode(Cues,Data.Options)},["candidates"]=new JsonArray(),
     ["primary_fingerprint"]=new JsonObject{["algorithm"]="primary-packets-sha256-v1",["video"]=new string((char)('a'+i),64),["audio"]=new string('f',64),["size"]=size,["mtime_ns"]=mtime,["sampled_digest"]=sample}});
   }
   var input=Path.Combine(Output,"input.json");var guide=Path.Combine(Output,"catalog.json");Data.Save(input,new{files=Rows});Data.Save(guide,new{episodes=Guide});
   Handler=new(reply);http=new(Handler);var matcher=new DialogueMatcher(http,new(){UseEvidenceRetrieval=true},beforeRequest:_=>{Starts++;return Task.CompletedTask;});
   Pipeline=new(matcher,(_,_,_)=>{Reads++;throw new IOException("Synthetic source needs fresh extraction");},subtitles?new SubtitleMatcher(new EmptyReferences(),Path.Combine(Output,"subtitles")):null);
   Settings=new(media,guide,Output,"m",Endpoint,DialogueInput:input,SelectedFiles:Rows.Select(r=>r.Text("path")).ToArray(),SeriesIdentity:new("tmdb","123","Example"));
  }
  public JsonObject Run(MatchSettings? settings=null,Progress? progress=null,CancellationToken ct=default)=>Data.Read(Pipeline.RunAsync(settings??Settings,progress??new(),ct).GetAwaiter().GetResult());
  public void Dispose(){http.Dispose();Handler.Dispose();ScoutStorage.Unregister(Output);}
 }
 public static void Run(Action<string,Action> test,string temp) {
  test("Title candidates use unique titles without trusting old provider numbering",()=>{
   Check(TitleCandidates.Find("Example.S01E04.Silver.Comet.mkv",Guide)?.Episodes.Single().Id==101);
   Check(TitleCandidates.Find("Example.S03E11.mkv",Guide)==null);
   Check(TitleCandidates.Find("Example.S01E04.Silver.Comet.Part.1.mkv",Guide)==null);
   Check(TitleCandidates.Find("Example.S01E04.Silver.Comet.mkv",[Guide[0],Guide[1] with{Title="Silver Comet"}])==null);
   Check(TitleCandidates.Find("Example.S01E04.Silver.Comet.mkv",[Guide[0] with{Overview=""}])==null);
   Check(TitleCandidates.Find("Example.S01E04.iWill.Date.Freddie.mkv",[Guide[0] with{Title="iWant to Date Freddie"}])!=null);
  });
  test("Title verification hides names and numbers and audits every original dialogue block in one request",()=>{
   using var handler=new Handler((_,_)=>Audit());using var http=new HttpClient(handler);var matcher=new DialogueMatcher(http,new(){UseEvidenceRetrieval=true,AllowExcerptVerification=true});
   var candidate=TitleCandidates.Find("Example.S01E04.Silver.Comet.mkv",Guide)!;
   var result=matcher.VerifyTitleCandidateAsync(Cues,candidate,"m",Endpoint).GetAwaiter().GetResult();
   Check(TitleCandidates.Accepted(result,candidate,Cues)&&handler.Requests.Count==1&&result["grounded_verification"].Int("passes")==1);
   var source=Source(handler.Requests.Single());var body=source.ToJsonString();
   Check(!body.Contains("Silver Comet")&&!body.Contains("S01E04")&&!body.Contains("Golden River")&&source["catalog"]==null);
   Check(Cues.All(c=>body.Contains(c.Text))&&result["grounded_verification"].Int("included_blocks")==DialogueMatcher.Blocks(Cues).Count);
   Check(!TitleCandidates.Accepted(result,candidate with{SourceName="different.mkv"},Cues));
   Check(!TitleCandidates.Accepted(result,candidate,[Cues[0] with{Text="changed"},..Cues.Skip(1)]));
   Check(!TitleCandidates.Accepted(result,candidate with{Episodes=[Guide[1]]},Cues));
  });
  foreach(var decision in new[]{"unresolved","incomplete_episode"})test("A filename cannot override dialogue coverage: "+decision,()=>{
   using var handler=new Handler((_,_)=>decision=="unresolved"?Unsupported():Audit(decision));using var http=new HttpClient(handler);var candidate=TitleCandidates.Find("Example.S01E04.Silver.Comet.mkv",Guide)!;
   var result=new DialogueMatcher(http).VerifyTitleCandidateAsync(Cues,candidate,"m",Endpoint).GetAwaiter().GetResult();Check(!TitleCandidates.Accepted(result,candidate,Cues));
  });
  test("Forged title-verification citations cannot establish an episode",()=>{
   using var handler=new Handler((_,_)=>{var r=Audit();r["candidates"]!["c0"]!["statements"]!["g0"]!["evidence"]=new JsonArray("s999");return r;});using var http=new HttpClient(handler);
   try{new DialogueMatcher(http).VerifyTitleCandidateAsync(Cues,TitleCandidates.Find("Silver Comet.mkv",Guide)!,"m",Endpoint).GetAwaiter().GetResult();throw new Exception("Forged quote accepted");}catch(InvalidDataException){}
  });
  foreach(bool disagreement in new[]{false,true})test("Multipart title candidates require two consistent complete dialogue audits: "+disagreement,()=>{
   var guide=new[]{Guide[0] with{Title="The Rescue (1)"},Guide[1] with{Title="The Rescue (2)"}};var candidate=TitleCandidates.Find("Example.S03E11E12.The.Rescue.mkv",guide)!;
   Check(candidate.Episodes.Length==2);using var handler=new Handler((_,n)=>n==2&&disagreement?Unsupported(2):Audit("continuous_parts",2));using var http=new HttpClient(handler);
   try{var r=new DialogueMatcher(http).VerifyTitleCandidateAsync(Cues,candidate,"m",Endpoint).GetAwaiter().GetResult();Check(!disagreement&&TitleCandidates.Accepted(r,candidate,Cues)&&handler.Requests.Count==2);}catch(InvalidDataException){Check(disagreement&&handler.Requests.Count==2);}
  });
  test("Title cache identity changes with source name guide dialogue model endpoint and generation",()=>{
   using var http=new HttpClient(new Handler((_,_)=>Audit()));var matcher=new DialogueMatcher(http);var candidate=TitleCandidates.Find("Silver Comet.mkv",Guide)!;
   string Signature(TitleCandidate? c=null,Cue[]? cues=null,Episode[]? guide=null,string model="m",string endpoint=Endpoint,string generation="one")=>TitleCandidates.Signature(c??candidate,cues??Cues,guide??Guide,matcher,model,endpoint,generation);
   var a=Signature();Check(a==Signature()&&new[]{Signature(candidate with{SourceName="new.mkv"}),Signature(cues:[Cues[0] with{Text="different"},..Cues.Skip(1)]),Signature(guide:[Guide[0] with{Overview="Changed synopsis"},Guide[1]]),Signature(model:"m2"),Signature(endpoint:"http://127.0.0.1:4321/v1/chat/completions"),Signature(generation:"two")}.All(s=>s!=a));
  });
  foreach(bool subtitles in new[]{true,false})test("All title candidates are checked before broad AI fallback: subtitles="+subtitles,()=>{
   using var f=new Fixture(temp,"order-"+subtitles,["Example.S01E04.Silver.Comet.mkv","Example.S01E05.Golden.River.mkv"],(request,n)=>{
    if(n==1){Check(Source(request)["catalog"]==null);return Unsupported();}
    if(n==2){Check(Source(request)["catalog"]==null);return Audit(offset:2);}
    Check(Source(request)["catalog"]!=null,"Broad search must follow both candidate checks");throw new HttpRequestException("Stop full fallback in isolated test");
   },subtitles);
   var log=new Progress();var r=f.Run(progress:log);Check(f.Starts==3&&f.Reads==0);
   var first=r.Array("files").Single(x=>x.Text("path")==f.Rows[0].Text("path"));var second=r.Array("files").Single(x=>x.Text("path")==f.Rows[1].Text("path"));
   Check(first.Array("candidates").Count==0&&!first!["title_verification_attempt"].Bool("accepted")&&second!["title_verification_attempt"].Bool("accepted")&&second.Array("candidates")[0]!["episode"].Int("id")==102);
   Check(log.Lines.FindIndex(l=>l.StartsWith("Match found:"))<log.Lines.FindIndex(l=>l.StartsWith("AI fallback [")));
  });
  test("AI-off retains title hints without model calls and issue review verifies them when enabled",()=>{
   using var f=new Fixture(temp,"off",["Example.S01E04.Silver.Comet.mkv"],(_,_)=>Audit());var r=f.Run(f.Settings with{UseAiFallback=false,Model="",Endpoint=""});
   var row=r.Array("files").Single()!;Check(f.Starts==0&&f.Handler.Requests.Count==0&&row["title_candidate"]!=null&&row.Array("candidates").Count==0&&row["reasoning"]==null&&row.Text("status")=="awaiting_ai");
   r=f.Run(f.Settings with{Restart=true,ReviewIssues=true});row=r.Array("files").Single()!;Check(f.Starts==1&&f.Reads==0&&row["title_verification_attempt"].Bool("accepted")&&row["conflict_review"]==null);
   var count=f.Starts;r=f.Run(f.Settings with{Restart=true});Check(f.Starts==count&&r.Array("files").Single()!.Array("candidates").Count==1);
  });
  test("Rejected title verification survives fallback errors and is reused on resume",()=>{
   using var f=new Fixture(temp,"cache",["Silver Comet.mkv"],(_,n)=>n==1?Unsupported():throw new HttpRequestException("Stop broad fallback"));
   f.Run();Check(f.Starts==2);var logs=new Progress();f.Run(f.Settings with{Restart=true},logs);Check(f.Starts==3&&logs.Lines.Any(l=>l.Contains("Reusing exact-input title verification")));
  });
  test("Shared-story coverage failure stays held without another full catalog search",()=>{
   using var f=new Fixture(temp,"coverage-only",["Example.S03E11E12.Silver.Comet.mkv"],(request,_)=>{
    Check(Source(request)["catalog"]==null,"Coverage-only failure must not trigger a broad search");
    var audit=Audit();audit["recording"]!["kind"]="unresolved";audit["recording"]!["candidates"]=new JsonArray();
    audit["installments"]=new JsonObject{["c0"]=new JsonArray(),["c1"]=new JsonArray()};return audit;
   });
   Data.Save(f.Settings.Catalog,new{episodes=new[]{Guide[0],Guide[0] with{Id=102,Number=12,Title="Silver Comet - Part 2"}}});
   var progress=new Progress();var row=f.Run(progress:progress).Array("files").Single()!;
    Check(f.Starts==2&&row.Array("candidates").Count==0&&row["reasoning"].Bool("coverage_review_only")&&row.Text("status")=="needs_review",
     "Calls="+f.Starts+"; status="+row.Text("status")+"; "+row.Text("error")+"; "+string.Join(" | ",progress.Lines));
   Check(progress.Lines.Any(l=>l.Contains("Skipping a redundant full-catalog search"))&&!progress.Lines.Any(l=>l.StartsWith("AI fallback [")));
  });
  test("A wrong filename can be rejected and replaced by a different dialogue-supported episode",()=>{
   using var f=new Fixture(temp,"wrong-name-corrected",["Example.S01E04.Silver.Comet.mkv"],(request,n)=>n switch {
    1=>Unsupported(),2=>new JsonObject{["candidates"]=new JsonArray(102),["search_more"]=false},_=>Audit(offset:2)});
   var row=f.Run().Array("files").Single()!;
   Check(f.Starts==3&&row.Array("candidates").Single()!["episode"].Int("id")==102&&!row["title_verification_attempt"].Bool("accepted")&&row["reasoning"]?["title_verification"]==null);
   Check(row["title_candidate"].Array("episodes").Single().Int("id")==101&&File.Exists(f.Rows[0].Text("path")));
  });
  test("Prior failed claims receive full-guide issue review instead of a title shortcut",()=>{
   using var f=new Fixture(temp,"previous-claim",["Silver Comet.mkv"],(request,_)=>{Check(Source(request)["catalog"]!=null,"A prior claim must use full-guide review");throw new HttpRequestException("Stop full-guide check");});
   var report=f.Run(f.Settings with{UseAiFallback=false});var row=report.Array("files").Single()!;
   row["candidates"]=new JsonArray(new JsonObject{["episode"]=JsonSerializer.SerializeToNode(Guide[1],Data.Options)});row["error"]="Previous identification failed";row["status"]="error";Data.Save(Path.Combine(f.Output,"report.json"),report);
   var result=f.Run(f.Settings with{Restart=true,ReviewIssues=true});Check(f.Starts==1&&result.Array("files").Single()!["title_verification_attempt"].Text("reason").Contains("full-guide"));
  });
  test("A transport failure during title verification does not start redundant full identification",()=>{
   using var f=new Fixture(temp,"transport",["Silver Comet.mkv"],(_,_)=>throw new HttpRequestException("Provider unavailable"));var row=f.Run().Array("files").Single()!;
   Check(f.Starts==1&&row.Array("candidates").Count==0&&row.Text("error").Contains("Provider unavailable"));
  });
  test("Changed sources are re-read before title verification",()=>{
   using var f=new Fixture(temp,"changed",["Silver Comet.mkv"],(_,_)=>throw new Exception("No model call allowed"));
   var logs=new Progress(s=>{if(s.StartsWith("Title verification: checking"))File.AppendAllText(f.Rows[0].Text("path"),"changed");});
   var r=f.Run(progress:logs);Check(f.Starts==0&&f.Reads==1&&r.Array("files").Single().Text("error").Contains("fresh extraction"));
  });
  test("Cancellation during title verification leaves resumable dialogue without an assignment",()=>{
   using var cancel=new CancellationTokenSource();using var f=new Fixture(temp,"cancel",["Silver Comet.mkv"],(_,_)=>{cancel.Cancel();throw new OperationCanceledException(cancel.Token);});
   try{f.Run(ct:cancel.Token);throw new Exception("Expected cancellation");}catch(OperationCanceledException){}
   var row=Data.Read(Path.Combine(f.Output,"report.json")).Array("files").Single()!;Check(row.Text("status")=="interrupted"&&row.Array("candidates").Count==0&&row["dialogue"]!=null&&f.Starts==1);
  });
 }
}
