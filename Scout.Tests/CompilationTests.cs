using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;

static class CompilationTests {
 sealed class Progress(Action<string> callback):IProgress<string>{public void Report(string value)=>callback(value);}
 public static void Run(Action<string,Action> test,string temp) {
  void Check(bool value,string message="Compilation assertion failed"){if(!value)throw new Exception(message);}
  var root=Path.Combine(temp,"compilations");Directory.CreateDirectory(root);
  Episode[] catalog=[new(1,1,1,"One"),new(2,1,2,"Two"),new(3,1,3,"Three")];
  List<Cue> Cues(int part)=>Enumerable.Range(0,40).Select(i=>new Cue(i*30,$"part{part} scene{i} person{i} location{i} detail{i} outcome{i} says distinctive dialogue about event{i}")).ToList();
  JsonObject Row(string name,int id,List<Cue> cues,double duration) {
   var path=Path.Combine(root,name);File.WriteAllText(path,name);
   return new(){["path"]=path,["duration"]=duration,["size"]=new FileInfo(path).Length,["source_mtime_ns"]=PreviewPlanner.MtimeNs(path),["sampled_digest"]=PreviewPlanner.SampledDigest(path),["full_dialogue"]=true,
    ["dialogue"]=new JsonObject{["cues"]=JsonSerializer.SerializeToNode(cues,Data.Options)},["matching_method"]="reasoning",
    ["candidates"]=new JsonArray(new JsonObject{["episode"]=JsonSerializer.SerializeToNode(catalog[id-1],Data.Options)}),
    ["primary_fingerprint"]=new JsonObject{["algorithm"]="full-file-sha256-v1",["sha256"]=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),["size"]=new FileInfo(path).Length,["mtime_ns"]=PreviewPlanner.MtimeNs(path),["sampled_digest"]=PreviewPlanner.SampledDigest(path)}};
  }
  JsonObject Report(int count=3) {
   var rows=Enumerable.Range(1,count).Select(i=>Row($"part{i}.mkv",i,Cues(i),1200)).ToList();
   rows.Add(Row("play-all.mkv",1,Enumerable.Range(1,count).SelectMany(i=>Cues(i).Select(c=>c with{Start=c.Start+(i-1)*1200})).ToList(),1200*count));
   return new(){["files"]=new JsonArray(rows.Select(r=>(JsonNode)r).ToArray())};
  }
  test("Exact play-all dialogue frees the individual episode claim without altering raw evidence",()=>{
   var report=Report();var before=report.ToJsonString();var groups=CollectionResolver.Resolve(report);
   Check(groups.Count(g=>g.Compilation!=null)==1&&groups.Count(g=>g.Episode!=null)==3);
   Check(groups.Single(g=>g.Compilation!=null).Claims.Count==0&&report.ToJsonString()==before);
   Check(!IssueReview.Select(report).Contains(report.Array("files")[3].Text("path")));
  });
  test("Timed-out compilations are classified without losing saved errors",()=>{
   var report=Report();var whole=report.Array("files")[3]!;whole["error"]="Timed out after 3600 seconds";
   var group=CollectionResolver.Resolve(report).Single(g=>g.Compilation!=null);
   Check(whole.Text("error").Contains("3600")&&group.Episodes.Count==0&&EvidenceReview.Create(group).Sections.Any(s=>s.Text.Contains("exact concatenation")));
  });
  test("Duration alone, partial transcripts and incomplete coverage cannot identify compilations",()=>{
   foreach(var kind in new[]{"duration","partial","altered","truncated","source_changed"}) {
    var report=Report();var whole=report.Array("files")[3]!;
    if(kind=="duration")whole["duration"]=4000;
    if(kind=="partial")whole["full_dialogue"]=false;
    if(kind=="altered")whole["dialogue"]!["cues"]![35]!["text"]="Different recording";
    if(kind=="truncated")whole["dialogue"]!["cues"]!.AsArray().RemoveAt(119);
    if(kind=="source_changed")whole["source_changed"]=true;
    Check(CollectionResolver.Resolve(report).All(g=>g.Compilation==null),kind);
   }
   Check(CollectionResolver.Resolve(Report(2)).All(g=>g.Compilation==null),"Two-part episodes must stay on multipart verification");
  });
  test("Explicit human assignments take precedence over derived compilation classification",()=>{
   var report=Report();var whole=report.Array("files")[3]!.AsObject();HumanResolution.Apply(report,whole,catalog[0],catalog,"Reviewed manually");
   var group=CollectionResolver.Resolve(report).Single(g=>g.Rows.Contains(whole));Check(group.HumanDecision!=null&&group.Compilation==null&&group.Episode?.Id==1);
  });
  test("Play-all is left in place by default and can be moved separately without episode names",()=>{
   var report=Report();var plan=PreviewPlanner.Create(report,root,"Show",catalog,contentOptions:new());var held=plan.Items.Single(i=>i.Category=="compilation");
   Check(held.Action=="hold"&&held.Destination==null&&plan.Items.Count(i=>i.Category=="episode")==3);
   var enabled=PreviewPlanner.Create(report,root,"Show",catalog,contentOptions:new(){IncludeCompilations=true}).Items.Single(i=>i.Category=="compilation");
   Check(enabled.Action=="rename/move"&&enabled.Destination!.Contains("Compilations")&&!enabled.Destination.Contains("S01E"));
  });
  test("Bonus options exclude known extras while preserving ordinary episodes",()=>{
   var report=Report();var extra=Row("bonus.mkv",1,Cues(5),1200);extra["candidates"]=new JsonArray();extra["reasoning"]=new JsonObject{["assessment"]="non_episode"};report.Array("files").Add(extra);
   var disabled=PreviewPlanner.Create(report,root,"Show",catalog,contentOptions:new());Check(disabled.Items.Single(i=>i.Source==extra.Text("path")).Category=="not_included");
   Check(PreviewPlanner.Create(report,root,"Show",catalog,contentOptions:new(){IncludeBonusContent=true}).Items.Single(i=>i.Source==extra.Text("path")).Destination!.Contains("Extras"));
   Check(!ScoutContentOptions.KnownBonus(report.Array("files")[0]!.AsObject()));
  });
  test("Pipeline prepares every transcript before inference, prioritizes episodes and excludes optional content",()=>{
   var report=Report();var bonus=Row("bonus-pipeline.mkv",1,Cues(7),1200);bonus["candidates"]=new JsonArray();bonus["reasoning"]=new JsonObject{["assessment"]="non_episode"};report.Array("files").Add(bonus);
   var shortEpisode=Row("short-unknown.mkv",1,Cues(8).Select(c=>c with{Start=c.Start/4}).ToList(),300);shortEpisode["candidates"]=new JsonArray();report.Array("files").Add(shortEpisode);
   var output=Path.Combine(temp,"compilation-pipeline");var reportPath=Path.Combine(output,"report.json");Data.Save(reportPath,report);
   var catalogPath=Path.Combine(output,"catalog.json");Data.Save(catalogPath,new{episodes=catalog.Select(e=>e with{Overview="A distinctive story about people encountering and resolving an unexpected problem."})});
   var handler=new FakeHandler();using var http=new HttpClient(handler);var logs=new List<string>();
   var pipeline=new MediaPipeline(new DialogueMatcher(http),(_,_,_)=>throw new Exception("Saved unchanged full dialogue needs no extraction"));
   pipeline.RunAsync(new(root,catalogPath,output,"model","http://127.0.0.1:1234/v1/chat/completions",SelectedFiles:report.Array("files").Select(r=>r.Text("path")).ToList(),Restart:true,Reanalyze:true,ContentOptions:new()),new Progress(logs.Add),default).GetAwaiter().GetResult();
   var saved=Data.Read(reportPath);var rows=saved.Array("files");
   Check(rows.Single(r=>r.Text("path").EndsWith("play-all.mkv")).Text("content_excluded")=="compilation");
   Check(rows.Single(r=>r.Text("path")==bonus.Text("path")).Text("content_excluded")=="bonus");
   Check(handler.Requests.Count==4,$"Expected three episode requests plus the unknown short recording, got {handler.Requests.Count}");
   Check(logs.FindIndex(l=>l.StartsWith("[1/6]"))>logs.FindLastIndex(l=>l.StartsWith("Reading dialogue")));
   Check(logs.First(l=>l.StartsWith("[1/6]")).Contains("part")&&logs.Single(l=>l.StartsWith("[4/6]")).Contains("short-unknown"));
   Check(ScoutContentOptions.Read(saved)==new ScoutContentOptions());
  });
  if(Environment.GetEnvironmentVariable("SCOUT_COMPILATION_REPORT") is string evidence) test("Isolated iCarly results identify eight compilations and release iGet Pranky",()=>{
   var report=Data.Read(evidence);var before=report.ToJsonString();var groups=CollectionResolver.Resolve(report);
   Check(groups.Count==96&&groups.Count(g=>g.Compilation!=null)==8&&groups.Count(g=>g.Episode!=null)==63&&groups.Count(g=>g.Category=="Unresolved")==11&&groups.Count(g=>g.NotProcessed)==14,
    $"Actual: {groups.Count} groups, {groups.Count(g=>g.Compilation!=null)} compilations, {groups.Count(g=>g.Episode!=null)} assigned, {groups.Count(g=>g.Category=="Unresolved")} unresolved");
   Check(groups.Single(g=>g.Episode?.Title=="iGet Pranky").Rows.Single()["duration"]!.GetValue<double>()<1500);
   Check(report.ToJsonString()==before);Console.WriteLine("iCarly rehearsal: 8 play-all files, 63 episode assignments, 11 unresolved, 14 without readable subtitles; saved source evidence unchanged.");
   var folder=Path.Combine(temp,"icarly-copy");var databasePath=Path.Combine(temp,"icarly-copy.db");ScoutStorage.Register(folder,new ScoutDatabase(databasePath,doggo:true));
   try {
    var path=Path.Combine(folder,"report.json");Data.Save(path,report);ScoutStorage.Unregister(folder);ScoutStorage.Register(folder,ScoutDatabase.OpenExistingDoggo(databasePath));
    Check(Data.Read(path).ToJsonString()==before&&CollectionResolver.Resolve(Data.Read(path)).Count(g=>g.Compilation!=null)==8,"Isolated Doggo roundtrip changed saved evidence");
   }finally{ScoutStorage.Unregister(folder);}
  });
 }
}
