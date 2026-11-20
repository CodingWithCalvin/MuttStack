using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;
using Scout.Core.Subtitles;

static class SubtitleIntegrationTests {
 public static void Run(Action<string,Action> test,string temp) {
  var series=new SeriesIdentity("tvdb","123","Example");
  Episode[] catalog=[new(101,3,11,"Silver Comet","The crew follows a comet."),new(102,3,12,"Golden River","The crew follows a river.")];
  List<Cue> Cues(string tag)=>Enumerable.Range(0,180).Select(i=>new Cue(i*5,$"{tag}{i} alpha{i} planet{i} nebula{i} captain{i} {tag}end{i}")).ToList();
  var first=Cues("first");var second=Cues("second");
  SubtitleReference Ref(string id,string title,List<Cue> cues)=>new(CollectionResolver.Hash(id),title,"https://dl.subdl.com/subtitle/example.zip","archive",3,cues);
  var set=new SubtitleReferenceSet(series,[Ref("first","Example.S03E12.Silver.Comet.srt",first),Ref("second","Example.S03E13.Golden.River.srt",second)]);
  void Assert(bool ok,string message="Subtitle integration check failed"){if(!ok)throw new Exception(message);}
  JsonObject Row(string folder,string name,List<Cue> cues,char fingerprint) {
   var path=Path.Combine(temp,folder,name);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,name);
   long size=new FileInfo(path).Length,mtime=PreviewPlanner.MtimeNs(path);var sample=PreviewPlanner.SampledDigest(path);
   return new(){["path"]=path,["duration"]=900.0,["full_dialogue"]=true,["size"]=size,["source_mtime_ns"]=mtime,["sampled_digest"]=sample,["dialogue"]=new JsonObject{["cues"]=JsonSerializer.SerializeToNode(cues,Data.Options)},["candidates"]=new JsonArray(),
    ["primary_fingerprint"]=new JsonObject{["algorithm"]="primary-packets-sha256-v1",["video"]=new string(fingerprint,64),["audio"]=new string('f',64),["size"]=size,["mtime_ns"]=mtime,["sampled_digest"]=sample}};
  }
  SubtitleAttempt Match(string folder,List<Cue> cues,Episode[]? guide=null,SubtitleReferenceSet? references=null) =>new SubtitleMatcher(new Source(references??set),Path.Combine(temp,folder)).IdentifyAsync(cues,guide??catalog,series,new Progress(),default).GetAwaiter().GetResult();
  test("Subtitle provider IDs survive different release numbering and evidence rejects edits",()=>{
   var result=Match("subtitle-id",first);var row=SubtitleResult.Row(first,catalog,result);
   Assert(SubtitleResult.Valid(row,catalog,series)&&row.Array("candidates")[0]!["episode"].Int("id")==101);
   Assert(!SubtitleResult.Valid(row,catalog,new("tmdb","123","Example")));
   Assert(!SubtitleResult.Valid(row,[catalog[0] with {Number=20},catalog[1]],series));
   row.Array("candidates")[0]!["episode"]!["id"]=102;Assert(!SubtitleResult.Valid(row,catalog,series));
   row=SubtitleResult.Row(first,catalog,result);row["dialogue"]!["cues"]![0]!["text"]="Changed dialogue";Assert(!SubtitleResult.Valid(row,catalog,series));
  });
  test("Subtitle results reuse database caches and invalidate on guide changes",()=>{
   string cache=Path.Combine(temp,"subtitle-db-cache");var db=new ScoutDatabase(Path.Combine(temp,"subtitle-test.db"));ScoutStorage.Register(cache,db);
   try {
    var source=new Source(set);var matcher=new SubtitleMatcher(source,cache);var logs=new Progress();
    var a=matcher.IdentifyAsync(first,catalog,series,logs,default).GetAwaiter().GetResult();
    var b=matcher.IdentifyAsync(first,catalog,series,logs,default).GetAwaiter().GetResult();
    Assert(source.Calls==1&&JsonNode.DeepEquals(a.Result,b.Result)&&logs.Lines.Any(l=>l.Contains("exact-input"))&&!Directory.Exists(cache));
    var changed=matcher.IdentifyAsync(first,[catalog[0] with {Title="Unrelated Title"},catalog[1]],series,logs,default).GetAwaiter().GetResult();Assert(changed.Result==null&&source.Calls==2);
   }finally{ScoutStorage.Unregister(cache);}
  });
  foreach(bool preflight in new[]{false,true})test($"Subtitle pass saves later matches before the first AI request (preflight={preflight})",()=>{
   var a=Row("subtitle-pipeline-media-"+preflight,"a.mkv",first,'a');var b=Row("subtitle-pipeline-media-"+preflight,"b.mkv",Cues("unrelated"),'b');a["duration"]=1200.0;
   var output=Path.Combine(temp,"subtitle-pipeline-output-"+preflight);var input=Path.Combine(output,"saved.json");var guide=Path.Combine(output,"catalog.json");
   Data.Save(input,new{files=new[]{a,b}});Data.Save(guide,new{series_identity=series,episodes=catalog});var before=ScoutStorage.ReadText(input);int starts=0;
   using var handler=new FailingModel();using var http=new HttpClient(handler);
   var matcher=new DialogueMatcher(http,beforeRequest:_=>{starts++;var saved=Data.Read(Path.Combine(output,"report.json")).Array("files").Single(r=>r.Text("path")==a.Text("path"))!.AsObject();Assert(SubtitleResult.Valid(saved,catalog,series),"Later subtitle match must be saved before AI loads");return Task.CompletedTask;});
   var pipeline=new MediaPipeline(matcher,(_,_,_)=>throw new Exception("Saved extraction/fingerprints must be reused"),new SubtitleMatcher(new Source(set),Path.Combine(output,"subtitles")));
   var settings=new MatchSettings(Path.GetDirectoryName(a.Text("path"))!,guide,output,"test-model","http://127.0.0.1:1234/v1/chat/completions",DialogueInput:input,SelectedFiles:[b.Text("path"),a.Text("path")],SeriesIdentity:series,ContentOptions:preflight?new():null);
   var progress=new Progress();var report=Data.Read(pipeline.RunAsync(settings,progress,default).GetAwaiter().GetResult());
   Assert(progress.Lines.Any(l=>l.StartsWith("Match found: a.mkv")&&l.Contains("S03E11 — Silver Comet")&&l.Contains("subtitles; collection checks pending")));
   Assert(progress.Lines.Any(l=>l.StartsWith("Matched: a.mkv")&&l.Contains("collection checks complete"))&&!progress.Lines.Any(l=>l.StartsWith("Matched: b.mkv")));
   Assert(IdentificationScope.Read(report)==new IdentificationScope("files"));
   var matched=report.Array("files").Single(r=>r.Text("path")==a.Text("path"))!.AsObject();Assert(SubtitleResult.Valid(matched,catalog,series)&&RunRecovery.CanReuse(matched,catalog,series));
   Assert(handler.Requests==1&&starts==1&&report.Array("files").Single(r=>r.Text("path")==b.Text("path")).Text("error")!=""&&ScoutStorage.ReadText(input)==before);
   Assert(progress.Lines.FindIndex(l=>l.StartsWith("Match found: a.mkv"))<progress.Lines.FindIndex(l=>l.StartsWith("AI fallback [1/1]")));
   Assert(!EvidenceReview.Create(CollectionResolver.Resolve(report).First(g=>g.Episode!=null)).Sections.Any(s=>s.Heading=="Model assessment"));
   var plan=PreviewPlanner.Create(report,settings.Root,series.Name,catalog,identity:series);Assert(plan.Items.Count==2&&File.Exists(a.Text("path"))&&File.Exists(b.Text("path")));
  });
  test("Subtitle-only runs retain unmatched dialogue and later issue review preserves successful matches",()=>{
   var a=Row("subtitle-optional-media","a.mkv",first,'a');var b=Row("subtitle-optional-media","b.mkv",Cues("unrelated"),'b');
   var output=Path.Combine(temp,"subtitle-optional-output");var input=Path.Combine(output,"input.json");var guide=Path.Combine(output,"catalog.json");
   Data.Save(input,new{files=new[]{a,b}});Data.Save(guide,new{episodes=catalog});
   using var handler=new FailingModel();using var http=new HttpClient(handler);int starts=0;
   var pipeline=new MediaPipeline(new DialogueMatcher(http,beforeRequest:_=>{starts++;return Task.CompletedTask;}),(_,_,_)=>throw new Exception("No extraction expected"),new SubtitleMatcher(new Source(set),Path.Combine(output,"cache")));
   var settings=new MatchSettings(Path.GetDirectoryName(a.Text("path"))!,guide,output,"","",DialogueInput:input,SelectedFiles:[b.Text("path"),a.Text("path")],SeriesIdentity:series,ContentOptions:new(),UseAiFallback:false);
   var progress=new Progress();var reportPath=pipeline.RunAsync(settings,progress,default).GetAwaiter().GetResult();var report=Data.Read(reportPath);
   var matched=report.Array("files").Single(r=>r.Text("path")==a.Text("path"))!.ToJsonString();var held=report.Array("files").Single(r=>r.Text("path")==b.Text("path"))!;
   Assert(handler.Requests==0&&starts==0&&held.Text("status")=="awaiting_ai"&&held.Text("error")==""&&held["reasoning"]==null&&held.Array("candidates").Count==0);
   Assert(JsonNode.DeepEquals(held["dialogue"],b["dialogue"])&&IssueReview.Select(report).SetEquals([b.Text("path")]));
   Assert(progress.Lines.Any(l=>l.Contains("Enable Use AI for unresolved files"))&&progress.Lines.Any(l=>l.StartsWith("Matched: a.mkv")));
   pipeline.RunAsync(settings with{Restart=true,ReviewIssues=true,UseAiFallback=true,Model="m",Endpoint="http://127.0.0.1:1234/v1/chat/completions"},new Progress(),default).GetAwaiter().GetResult();
   Assert(handler.Requests==1&&starts==1&&Data.Read(reportPath).Array("files").Single(r=>r.Text("path")==a.Text("path"))!.ToJsonString()==matched);
  });
  test("AI-off runs hold competing subtitle claims without loading conflict or claim review models",()=>{
   var a=Row("subtitle-conflict-off-media","a.mkv",first,'a');var b=Row("subtitle-conflict-off-media","b.mkv",first.Select((c,i)=>i==50?c with{Text=c.Text+" additional distinct words"}:c).ToList(),'b');
   var output=Path.Combine(temp,"subtitle-conflict-off-output");var input=Path.Combine(output,"input.json");var guide=Path.Combine(output,"catalog.json");Data.Save(input,new{files=new[]{a,b}});Data.Save(guide,new{episodes=catalog});
   using var handler=new FailingModel();using var http=new HttpClient(handler);int starts=0;
   var pipeline=new MediaPipeline(new DialogueMatcher(http,beforeRequest:_=>{starts++;return Task.CompletedTask;}),(_,_,_)=>throw new Exception("No extraction expected"),new SubtitleMatcher(new Source(set),Path.Combine(output,"cache")));
   var settings=new MatchSettings(Path.GetDirectoryName(a.Text("path"))!,guide,output,"","",DialogueInput:input,SelectedFiles:[a.Text("path"),b.Text("path")],SeriesIdentity:series,UseAiFallback:false);
   var path=pipeline.RunAsync(settings,new Progress(),default).GetAwaiter().GetResult();
   Assert(CollectionResolver.Resolve(Data.Read(path)).All(g=>g.Status=="unresolved_conflict"&&g.Episode==null),"Initial conflicting matches: "+string.Join("; ",Data.Read(path).Array("files").Select(r=>r.Text("status")+" "+r.Text("error")+" "+r?["subtitle_match"].Text("reason"))));
   pipeline.RunAsync(settings with{Restart=true,ReviewIssues=true},new Progress(),default).GetAwaiter().GetResult();
   Assert(handler.Requests==0&&starts==0&&CollectionResolver.Resolve(Data.Read(path)).All(g=>g.Status=="unresolved_conflict"&&g.Episode==null),"Review: "+string.Join("; ",CollectionResolver.Resolve(Data.Read(path)).Select(g=>g.Status))+" starts="+starts);
  });
  test("A deferred recording changed before AI is re-read instead of using stale dialogue",()=>{
   var a=Row("subtitle-changed-media","a.mkv",first,'a');var b=Row("subtitle-changed-media","b.mkv",Cues("unrelated"),'b');
   var output=Path.Combine(temp,"subtitle-changed-output");var input=Path.Combine(output,"input.json");var guide=Path.Combine(output,"catalog.json");Data.Save(input,new{files=new[]{a,b}});Data.Save(guide,new{episodes=catalog});
   using var handler=new FailingModel();using var http=new HttpClient(handler);int reads=0;
   var pipeline=new MediaPipeline(new DialogueMatcher(http),(_,_,_)=>{reads++;throw new IOException("Changed source needs fresh dialogue");},new SubtitleMatcher(new Source(set),Path.Combine(output,"cache")));
   var progress=new Progress(s=>{if(s.StartsWith("Subtitle pass complete. AI fallback:"))File.AppendAllText(b.Text("path")," changed");});
   var path=pipeline.RunAsync(new(Path.GetDirectoryName(a.Text("path"))!,guide,output,"m","http://127.0.0.1:1234/v1/chat/completions",DialogueInput:input,SelectedFiles:[b.Text("path"),a.Text("path")],SeriesIdentity:series),progress,default).GetAwaiter().GetResult();
   var held=Data.Read(path).Array("files").Single(r=>r.Text("path")==b.Text("path"))!;Assert(reads==1&&handler.Requests==0&&held.Text("error").Contains("Changed source")&&held.Array("candidates").Count==0);
  });
  test("Successful subtitle-only resume avoids reference access and model initialization",()=>{
   var row=Row("subtitle-resume-media","resume.mkv",first,'a');SubtitleResult.Apply(row,catalog,Match("subtitle-resume-match",first));
   var output=Path.Combine(temp,"subtitle-resume-output");var guide=Path.Combine(output,"catalog.json");var scope=new IdentificationScope("season",Path.GetDirectoryName(row.Text("path")));Data.Save(guide,new{series_identity=series,episodes=catalog});Data.Save(Path.Combine(output,"report.json"),new{series_identity=series,identification_scope=scope,files=new[]{row}});
   using var http=new HttpClient(new FailingModel());var matcher=new DialogueMatcher(http,beforeRequest:_=>throw new Exception("AI must remain unloaded"));
   var source=new Source(set){Fail=true};var pipeline=new MediaPipeline(matcher,(_,_,_)=>throw new Exception("No media extraction"),new SubtitleMatcher(source,Path.Combine(output,"subtitles")));
   var settings=new MatchSettings(Path.GetDirectoryName(row.Text("path"))!,guide,output,"","",SelectedFiles:[row.Text("path")],SeriesIdentity:series,Restart:true,Scope:new("show"));
   pipeline.RunAsync(settings,new Progress(),default).GetAwaiter().GetResult();Assert(source.Calls==0&&JsonNode.DeepEquals(Data.Read(Path.Combine(output,"report.json")).Array("files")[0],row));
   Assert(IdentificationScope.Read(Data.Read(Path.Combine(output,"report.json")))==scope);
  });
  test("Reanalysis accepts cached subtitle matches without shared JSON ownership errors or AI",()=>{
   var row=Row("subtitle-reanalyze-cache-media","a.mkv",first,'a');var output=Path.Combine(temp,"subtitle-reanalyze-cache-output");var input=Path.Combine(output,"input.json");var guide=Path.Combine(output,"catalog.json");Data.Save(input,new{files=new[]{row}});Data.Save(guide,new{episodes=catalog});
   using var handler=new FailingModel();using var http=new HttpClient(handler);
   var pipeline=new MediaPipeline(new DialogueMatcher(http),(_,_,_)=>throw new Exception("No extraction expected"),new SubtitleMatcher(new Source(set),Path.Combine(output,"cache")));
   var settings=new MatchSettings(Path.GetDirectoryName(row.Text("path"))!,guide,output,"","",DialogueInput:input,SelectedFiles:[row.Text("path")],SeriesIdentity:series,UseAiFallback:false);
   pipeline.RunAsync(settings,new Progress(),default).GetAwaiter().GetResult();var progress=new Progress();var path=pipeline.RunAsync(settings with{Restart=true,Reanalyze=true},progress,default).GetAwaiter().GetResult();
   var saved=Data.Read(path).Array("files").Single()!.AsObject();Assert(SubtitleResult.Valid(saved,catalog,series)&&saved.Text("error")==""&&handler.Requests==0&&progress.Lines.Any(l=>l.Contains("exact-input subtitle comparison")));
  });
  test("Subtitle results preserve duplicate grouping and hold distinct competing recordings",()=>{
   var a=Row("subtitle-duplicates","a.mkv",first,'a');var b=Row("subtitle-duplicates","b.mkv",first,'b');var result=Match("subtitle-duplicates-match",first);
   SubtitleResult.Apply(a,catalog,result);SubtitleResult.Apply(b,catalog,result);
   JsonObject Report()=>new(){["series_identity"]=JsonSerializer.SerializeToNode(series,Data.Options),["files"]=new JsonArray(a.DeepClone(),b.DeepClone())};
   var groups=CollectionResolver.Resolve(Report());Assert(groups.Count==1&&groups[0].Copies==2&&groups[0].Episode?.Id==101);
   var edited=first.Select((c,i)=>i==50?c with{Text=c.Text+" distinct extra phrase in this recording"}:c).ToList();
   b["dialogue"]!["cues"]=JsonSerializer.SerializeToNode(edited,Data.Options);SubtitleResult.Apply(b,catalog,Match("subtitle-distinct-match",edited));
   Assert(CollectionResolver.Resolve(Report()).All(g=>g.Status=="unresolved_conflict"&&g.Episode==null));
  });
  test("Combined subtitle assignments validate provider parts and produce multi-episode previews",()=>{
   Episode[] guide=[new(201,1,1,"Long Journey (1)"),new(202,1,2,"Long Journey (2)")];
   var cues=first.Concat(second.Select(c=>c with{Start=c.Start+900})).ToList();var refs=new SubtitleReferenceSet(series,[Ref("combined","Example.S01E01-E02.Long.Journey.srt",cues)]);
   var row=Row("subtitle-combined","combined.mkv",cues,'a');row["duration"]=1800.0;SubtitleResult.Apply(row,guide,Match("subtitle-combined-match",cues,guide,refs));
   Assert(SubtitleResult.Valid(row,guide,series)&&MultipartEvidence.Combined(row));
   var report=new JsonObject{["series_identity"]=JsonSerializer.SerializeToNode(series,Data.Options),["files"]=new JsonArray(row)};
   Assert(CollectionResolver.Resolve(report).Single().Combined);
   var plan=PreviewPlanner.Create(report,Path.GetDirectoryName(row.Text("path"))!,series.Name,guide,identity:series);Assert(plan.Items.Single().Destination?.Contains("S01E01-E02")==true);
   Assert(plan.Items.Single().Destination?.EndsWith("Long Journey.mkv")==true&&SubtitleResult.Valid(row,guide,series));
  });
  test("Cancelled subtitle processing does not invoke AI and retains resumable evidence",()=>{
   var row=Row("subtitle-cancel-media","cancel.mkv",first,'a');var output=Path.Combine(temp,"subtitle-cancel-output");var input=Path.Combine(output,"input.json");var guide=Path.Combine(output,"catalog.json");Data.Save(input,new{files=new[]{row}});Data.Save(guide,new{episodes=catalog});
   using var cancel=new CancellationTokenSource();var source=new Source(set){OnLoad=()=>cancel.Cancel()};using var handler=new FailingModel();using var http=new HttpClient(handler);
   var pipeline=new MediaPipeline(new DialogueMatcher(http),(_,_,_)=>throw new Exception("No extraction expected"),new SubtitleMatcher(source,Path.Combine(output,"cache")));
   try{pipeline.RunAsync(new(Path.GetDirectoryName(row.Text("path"))!,guide,output,"m","http://127.0.0.1:1234/v1/chat/completions",DialogueInput:input,SelectedFiles:[row.Text("path")],SeriesIdentity:series),new Progress(),cancel.Token).GetAwaiter().GetResult();throw new Exception("Cancellation expected");}catch(OperationCanceledException){}
   var saved=Data.Read(Path.Combine(output,"report.json")).Array("files").Single()!;Assert(handler.Requests==0&&saved.Text("status")=="interrupted"&&saved["dialogue"].Array("cues").Count==first.Count);
  });
  test("SubDL provider identity and references are cached in the database without credential disclosure",()=>{
   var cache=Path.Combine(temp,"subtitle-download-cache");var db=new ScoutDatabase(Path.Combine(temp,"subtitle-download.db"));ScoutStorage.Register(cache,db);int requests=0;
   try {
    using var zipBytes=new MemoryStream();using(var zip=new ZipArchive(zipBytes,ZipArchiveMode.Create,true))using(var writer=new StreamWriter(zip.CreateEntry("Example.S03E12.Silver.Comet.srt").Open()))writer.Write(string.Join("\n\n",first.Select((c,i)=>$"{i+1}\n{TimeSpan.FromSeconds(c.Start):hh\\:mm\\:ss},000 --> {TimeSpan.FromSeconds(c.Start+2):hh\\:mm\\:ss},000\n{c.Text}")));
    HttpMessageHandler Api()=>new Response(r=>{requests++;var url=r.RequestUri!;if(url.Host=="dl.subdl.com")return new(HttpStatusCode.OK){Content=new ByteArrayContent(zipBytes.ToArray())};return Json(url.AbsolutePath.EndsWith("movies/search")?"""{"results":[{"sd_id":"sd123","imdb_id":"tt999","type":"tv"}]}""":"""{"subtitles":[{"lang":"english","url":"/subtitle/example.zip","unpack_files":["Example.S03E12.Silver.Comet.srt"]}]}""");});
    HttpMessageHandler Metadata()=>new Response(r=>Json("""{"id":4,"externals":{"thetvdb":123,"imdb":"tt999"}}"""));
    var loaded=new SubdlReferences(cache,"fake-subtitle-key",apiHandler:Api,metadataHandler:Metadata).LoadAsync(series,catalog,new Progress(),default).GetAwaiter().GetResult();Assert(loaded.References.Count==1&&requests==3);
    var reused=new SubdlReferences(cache,null,apiHandler:()=>throw new Exception("No network expected")).LoadAsync(series,catalog,new Progress(),default).GetAwaiter().GetResult();Assert(reused.References.Count==1&&!Directory.Exists(cache));
    var documents=ScoutStorage.List(cache,"manifest.json").ToList();Assert(documents.Count==1);
    documents.AddRange(Data.Read(documents[0]).Array("references").Select(id=>Path.Combine(Path.GetDirectoryName(documents[0])!,"references",id!.GetValue<string>()+".json")));
    Assert(documents.All(p=>!ScoutStorage.ReadText(p).Contains("fake-subtitle-key")));
    var wrong=new SubdlReferences(Path.Combine(cache,"wrong"),"fake-key",apiHandler:Api,metadataHandler:()=>new Response(_=>Json("""{"id":4,"externals":{"thetvdb":456,"imdb":"tt999"}}"""))).LoadAsync(series,catalog,new Progress(),default).GetAwaiter().GetResult();Assert(wrong.References.Count==0&&requests==3);
   }finally{ScoutStorage.Unregister(cache);}
  });
 }
 static HttpResponseMessage Json(string value)=>new(HttpStatusCode.OK){Content=new StringContent(value)};
 sealed class Source(SubtitleReferenceSet value):ISubtitleReferenceSource {
  public int Calls;public bool Fail;public Action? OnLoad;
  public Task<SubtitleReferenceSet> LoadAsync(SeriesIdentity series,IReadOnlyList<Episode> catalog,IProgress<string> progress,CancellationToken ct){Calls++;OnLoad?.Invoke();ct.ThrowIfCancellationRequested();if(Fail)throw new Exception("Reference source must not be called");return Task.FromResult(value);}
 }
 sealed class Progress(Action<string>? onReport=null):IProgress<string>{public List<string> Lines=[];public void Report(string value){Lines.Add(value);onReport?.Invoke(value);}}
 sealed class FailingModel:HttpMessageHandler {public int Requests;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct){Requests++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));}}
 sealed class Response(Func<HttpRequestMessage,HttpResponseMessage> respond):HttpMessageHandler {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)=>Task.FromResult(respond(r));}
}
