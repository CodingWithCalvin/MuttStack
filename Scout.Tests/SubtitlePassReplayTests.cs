using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;
using Scout.Core.Subtitles;

static class SubtitlePassReplayTests {
 public static void Run(Action<string,Action> test,string temp) {
  if(Environment.GetEnvironmentVariable("SCOUT_SUBTITLE_REPLAY") is not string fixture)return;
  test("Isolated 34-file iCarly pipeline saves six subtitle matches before optional AI starts",()=>{
   void Check(bool value,string message){if(!value)throw new Exception(message);}
   var original=Data.Read(Path.Combine(fixture,"6e5bdca7626b4fde987a2b0ff343c39a-report.json"));
   var series=original["series_identity"]!.Deserialize<SeriesIdentity>(Data.Options)!;
   var guide=Data.Catalog(Path.Combine(fixture,"catalog.json"));
   var refs=JsonSerializer.Deserialize<List<SubtitleReference>>(File.ReadAllText(Path.Combine(fixture,"enriched-references.json")),Data.Options)!;
   var root=Path.Combine(temp,"subtitle-pass-replay-media");Directory.CreateDirectory(root);
   var output=Path.Combine(temp,"subtitle-pass-replay-output");var input=Path.Combine(output,"input.json");var catalog=Path.Combine(output,"catalog.json");
   var rows=new JsonArray();
   foreach(var saved in original.Array("files")) {
    // Original paths are never opened. Real dialogue is attached to synthetic
    // files with their own fingerprints, so the pipeline uses only this fixture.
    var path=Path.Combine(root,$"{rows.Count:00}-"+Path.GetFileName(saved.Text("path")));File.WriteAllText(path,Path.GetFileName(path));
    long size=new FileInfo(path).Length,mtime=PreviewPlanner.MtimeNs(path);var sample=PreviewPlanner.SampledDigest(path);
    rows.Add(new JsonObject{["path"]=path,["duration"]=saved!["duration"]?.DeepClone(),["dialogue"]=saved["dialogue"]!.DeepClone(),["transcription"]=saved["transcription"]?.DeepClone(),
     ["full_dialogue"]=true,["size"]=size,["source_mtime_ns"]=mtime,["sampled_digest"]=sample,["candidates"]=new JsonArray(),
     ["primary_fingerprint"]=new JsonObject{["algorithm"]="primary-packets-sha256-v1",["video"]=CollectionResolver.Hash(path),["audio"]=new string('f',64),["size"]=size,["mtime_ns"]=mtime,["sampled_digest"]=sample}});
   }
   Data.Save(input,new JsonObject{["files"]=rows});Data.Save(catalog,new{episodes=guide});
   using var cancel=new CancellationTokenSource();int starts=0;
   using var http=new HttpClient(new NoNetwork());
   var pipeline=new MediaPipeline(new DialogueMatcher(http,beforeRequest:ct=>{starts++;cancel.Cancel();ct.ThrowIfCancellationRequested();return Task.CompletedTask;}),
    (_,_,_)=>throw new Exception("Saved fixture dialogue and fingerprints must be reused"),
    new SubtitleMatcher(new Source(new(series,refs)),Path.Combine(output,"cache")),new SavedSpeech(rows.First(r=>r!["transcription"]!=null)!["transcription"].Text("configuration")));
   var settings=new MatchSettings(root,catalog,output,"test","http://127.0.0.1:1234/v1/chat/completions",DialogueInput:input,SelectedFiles:rows.Select(r=>r.Text("path")).ToList(),SeriesIdentity:series,ContentOptions:new(),UseAiFallback:false);
   var pathReport=pipeline.RunAsync(settings,new Progress(),default).GetAwaiter().GetResult();var report=Data.Read(pathReport);
   var episodeGuide=guide.Where(e=>e.Season>0).ToList();
   var matched=report.Array("files").OfType<JsonObject>().Where(r=>SubtitleResult.Valid(r,episodeGuide,series)).ToDictionary(r=>r.Text("path"),r=>r.ToJsonString());
   Check(starts==0&&matched.Count==6&&report.Array("files").Count(r=>r.Text("status")=="awaiting_ai")==28,$"Expected six saved matches, 28 deferred files and no AI; got {matched.Count} matches, {report.Array("files").Count(r=>r.Text("status")=="awaiting_ai")} deferred, {starts} AI starts. "+string.Join("; ",report.Array("files").Where(r=>r.Text("error")!="").Select(r=>Path.GetFileName(r.Text("path"))+": "+r.Text("error"))));
   Check(IssueReview.Select(report).Count==28,"Only the unresolved files should enter issue review");
   try{pipeline.RunAsync(settings with{Restart=true,ReviewIssues=true,UseAiFallback=true},new Progress(),cancel.Token).GetAwaiter().GetResult();throw new Exception("Expected cancellation at the first AI request");}catch(OperationCanceledException){}
   report=Data.Read(pathReport);
   Check(starts==1&&matched.All(p=>report.Array("files").Single(r=>r.Text("path")==p.Key)!.ToJsonString()==p.Value),"Enabling AI must keep every successful match unchanged");
   Console.WriteLine("iCarly subtitle-pass rehearsal: 34 synthetic sources, 6 saved subtitle matches, 28 deferred; successful matches unchanged when AI review starts.");
  });
 }
 sealed class Progress:IProgress<string>{public void Report(string value){}}
 sealed class Source(SubtitleReferenceSet set):ISubtitleReferenceSource{public Task<SubtitleReferenceSet> LoadAsync(SeriesIdentity s,IReadOnlyList<Episode> e,IProgress<string> p,CancellationToken ct)=>Task.FromResult(set);}
 sealed class SavedSpeech(string key):ISpeechTranscriber {
  public string ConfigurationKey=>key;
  public Task<SpeechTranscript> TranscribeAsync(SpeechRequest r,IProgress<string> p,CancellationToken ct,ScoutPause? pause=null)=>throw new Exception("Use saved transcription only");
 }
 sealed class NoNetwork:HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)=>throw new Exception("No network or model requests in replay");}
}
