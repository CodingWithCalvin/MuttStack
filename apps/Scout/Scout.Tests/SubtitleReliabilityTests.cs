using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;
using Scout.Core.Subtitles;

static class SubtitleReliabilityTests {
 static void Check(bool value,string message="Subtitle reliability regression"){if(!value)throw new Exception(message);}
 sealed class Progress:IProgress<string>{public List<string> Lines=[];public void Report(string message)=>Lines.Add(message);}
 sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> send):HttpMessageHandler {
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(send(request));
 }
 sealed class Source(SubtitleReferenceSet set):ISubtitleReferenceSource {
  public Task<SubtitleReferenceSet> LoadAsync(SeriesIdentity s,IReadOnlyList<Episode> c,IProgress<string> p,CancellationToken ct)=>Task.FromResult(set);
 }
 static HttpResponseMessage Json(JsonObject body)=>new(HttpStatusCode.OK){Content=new StringContent(body.ToJsonString())};
 static JsonObject Empty()=>new(){["data"]=new JsonArray(),["page"]=1,["total_pages"]=0};
 static string Query(HttpRequestMessage r)=>r.RequestUri!.Query;
 public static void Run(Action<string,Action> test,string temp) {
  foreach(var status in new[]{HttpStatusCode.RequestTimeout,HttpStatusCode.InternalServerError,HttpStatusCode.BadGateway,HttpStatusCode.ServiceUnavailable,HttpStatusCode.GatewayTimeout})test("OpenSubtitles retries transient GET failures with bounded backoff: "+status,()=>{
   int calls=0;var waits=new List<TimeSpan>();var logs=new Progress();
   using var api=new OpenSubtitlesApi(new("fake-key"),new Handler(_=>++calls<3?new(status):Json(Empty())),logs,(d,_)=>{waits.Add(d);return Task.CompletedTask;});
   Check(api.Search("parent_tmdb_id","123",1,1,default).GetAwaiter().GetResult().Array("data").Count==0);
   Check(calls==3&&waits.SequenceEqual(new[]{TimeSpan.FromSeconds(1),TimeSpan.FromSeconds(2)})&&logs.Lines.Count==2);
  });
  test("OpenSubtitles stops after three failed read attempts and never retries a download POST",()=>{
   int calls=0;using var api=new OpenSubtitlesApi(new("fake-key"),new Handler(_=>{calls++;return new(HttpStatusCode.ServiceUnavailable);}),wait:(_,_)=>Task.CompletedTask);
   try{api.Search("parent_tmdb_id","123",1,1,default).GetAwaiter().GetResult();throw new Exception("Expected failure");}catch(OpenSubtitlesAccessException){}
   Check(calls==3);calls=0;
   try{api.Download(123,default).GetAwaiter().GetResult();throw new Exception("Expected failure");}catch(OpenSubtitlesAccessException){}
   Check(calls==1);
  });
  test("OpenSubtitles retries transport and timeout failures but propagates cancellation",()=>{
   foreach(bool timeout in new[]{false,true}) {
    int calls=0;using var api=new OpenSubtitlesApi(new("fake-key"),new Handler(_=>{if(++calls==1){if(timeout)throw new TaskCanceledException();throw new HttpRequestException();}return Json(Empty());}),wait:(_,_)=>Task.CompletedTask);
    api.Search("parent_tmdb_id","123",1,1,default).GetAwaiter().GetResult();Check(calls==2);
   }
   using var cancel=new CancellationTokenSource();int requests=0;
   using var cancelled=new OpenSubtitlesApi(new("fake-key"),new Handler(_=>{requests++;return new(HttpStatusCode.ServiceUnavailable);}),wait:(_,ct)=>{cancel.Cancel();ct.ThrowIfCancellationRequested();return Task.CompletedTask;});
   try{cancelled.Search("parent_tmdb_id","123",1,1,cancel.Token).GetAwaiter().GetResult();throw new Exception("Expected cancellation");}catch(OperationCanceledException){}Check(requests==1);
  });
  test("Retry-After is honored and long pauses do not cause immediate retries",()=>{
   foreach(int seconds in new[]{4,120}) {
    int calls=0;var waits=new List<TimeSpan>();
    using var api=new OpenSubtitlesApi(new("fake-key"),new Handler(_=>{calls++;if(calls>1)return Json(Empty());var r=new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);r.Headers.RetryAfter=new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));return r;}),wait:(d,_)=>{waits.Add(d);return Task.CompletedTask;});
    try{api.Search("parent_tmdb_id","123",1,1,default).GetAwaiter().GetResult();Check(seconds==4);}catch(OpenSubtitlesAccessException){Check(seconds==120);}
    Check(calls==(seconds==4?2:1)&&waits.Count==(seconds==4?1:0));if(waits.Count>0)Check(waits[0]>TimeSpan.FromSeconds(3));
   }
  });
  test("A failed OpenSubtitles season does not prevent later seasons and failures are retried on the next run",()=>{
   var folder=Path.Combine(temp,"open-season-isolation");var series=new SeriesIdentity("tmdb","123","Example");Episode[] guide=[new(1,1,1,"One"),new(2,3,1,"Three")];
   var requests=new List<string>();bool fail=true;var progress=new Progress();
   OpenSubtitlesReferences Make()=>new(folder,new("fake-key"),()=>new Handler(r=>{requests.Add(Query(r));return Query(r).Contains("season_number=1")&&fail?new(HttpStatusCode.ServiceUnavailable):Json(Empty());}));
   var first=Make().LoadAsync(series,guide,progress,default).GetAwaiter().GetResult();
   Check(first.References.Count==0&&first.Note.Contains("season 01 unavailable")&&requests.Count==4&&requests.Last().Contains("season_number=3"));
   fail=false;requests.Clear();Make().LoadAsync(series,guide,progress,default).GetAwaiter().GetResult();
   Check(requests.Count==1&&requests[0].Contains("season_number=1")&&progress.Lines.Any(l=>l.Contains("cached subtitle search")));
  });
  test("Successful empty search results are reused, expire, and errors never become cached misses",()=>{
   var folder=Path.Combine(temp,"search-reuse");int calls=0;var log=new Progress();
   JsonObject Fetch(string key)=>SubtitleSearchCache.Get(folder,key,()=>{calls++;return Task.FromResult(Empty());},r=>r["data"] is JsonArray,log,default).GetAwaiter().GetResult();
   Fetch("tmdb:1:season:2");Fetch("tmdb:1:season:2");Check(calls==1);
   Fetch("tmdb:2:season:2");Check(calls==2);
   var cached=Directory.GetFiles(Path.Combine(folder,"searches-v1")).Single(p=>Data.Read(p).Text("key")=="tmdb:1:season:2");var data=Data.Read(cached);data["updated"]=DateTimeOffset.UtcNow.AddHours(-25).ToString("O");Data.Save(cached,data);
   Fetch("tmdb:1:season:2");Check(calls==3);
   try{SubtitleSearchCache.Get(folder,"failed",()=>throw new HttpRequestException(),_=>true,log,default).GetAwaiter().GetResult();}catch(HttpRequestException){}
   Check(Directory.GetFiles(Path.Combine(folder,"searches-v1")).Length==2);
   File.WriteAllText(cached,"broken");Fetch("tmdb:1:season:2");Check(calls==4);
  });
  test("Unspoken episode titles can be mapped only with complete distinctive plot evidence",()=>{
   var series=new SeriesIdentity("tmdb","123","Example");Episode[] guide=[new(1,1,1,"A Day to Remember","The stranded astronaut escapes the lunar crater using a silver ladder and a magnetic tether."),new(2,1,2,"A Quiet Morning","The gardener plants purple tulips beside a wooden greenhouse.")];
   var cues=Enumerable.Range(0,180).Select(i=>new Cue(i*5,$"astronaut lunar crater silver ladder magnetic tether event{i} action{i} ending{i}")).ToList();
   var reference=new SubtitleReference("ref","Example.S03E12.srt","fixture","archive",3,cues);
   SubtitleAttempt Match(string key,string name,List<Cue>? dialogue=null,Episode[]? catalog=null)=>new SubtitleMatcher(new Source(new(series,[reference])),Path.Combine(temp,"plot-proof",key)).IdentifyAsync(dialogue??cues,catalog??guide,series,new Progress(),default,new(name)).GetAwaiter().GetResult();
   const string name="Example.S09E03.A.Day.to.Remember.mkv";
   var matched=Match("right",name);Check(matched.Result!=null&&matched.Details["best"]!["link"].Text("method")=="source-title-with-distinctive-dialogue-plot");
   Check(Match("wrong","Example.S01E02.A.Quiet.Morning.mkv").Result==null);
   Check(Match("no-title","Example.S01E01.mkv").Result==null);
   Check(Match("partial",name,cues.Take(60).ToList()).Result==null);
   Check(Match("combined-label","Example.S01E01E02.A.Day.to.Remember.mkv").Result==null);
   Check(Match("tied",name,catalog:[..guide,new(3,2,1,"Another Day",guide[0].Overview)]).Result==null);
   Check(Match("no-guide",name,catalog:guide.Select(e=>e with{Overview=""}).ToArray()).Result==null);
   // Full matching subtitles and an agreeing filename are insufficient when
   // the distinctive plot appears only in a recap at the start.
   var opening=cues.Select((c,i)=>i<3?c:c with{Text=$"unrelated{i} talk{i} line{i} event{i} ending{i}"}).ToList();
   var earlyRef=reference with{Cues=opening};
   var early=new SubtitleMatcher(new Source(new(series,[earlyRef])),Path.Combine(temp,"plot-opening"));
   Check(early.IdentifyAsync(opening,guide,series,new Progress(),default,new(name)).GetAwaiter().GetResult().Result==null);
  });
  test("Subtitle evidence without an episode identity has an accurate status and no false AI or missing-evidence message",()=>{
   var details=new JsonObject{["accepted"]=false,["text_supported"]=true,["reason"]="Dialogue matches, but a unique provider title/ID mapping is missing or conflicting."};
   var row=new JsonObject{["path"]=Path.Combine(temp,"status.mkv"),["subtitle_match"]=details,["candidates"]=new JsonArray(),["dialogue"]=new JsonObject{["cues"]=new JsonArray(new JsonObject{["start"]=0.0,["text"]="Readable dialogue"})}};
   var group=new RecordingGroup{Id="test",Rows=[row],Status="unassigned"};var review=EvidenceReview.Create(group);
   Check(group.DisplayStatus=="Subtitle matched — episode identity unresolved"&&review.Assessment==group.DisplayStatus);
   Check(review.Sections.Any(s=>s.Heading=="Subtitle comparison")&&!review.Sections.Any(s=>s.Heading is "Subtitle stage before AI" or "Evidence unavailable"));
   details["text_supported"]=false;details["reason"]="No reference covers enough distinctive dialogue.";Check(group.DisplayStatus=="No matching subtitle reference");
   details["reason"]="Subtitle lookup or validation was unavailable.";Check(group.DisplayStatus=="Subtitle lookup unavailable");
   details["reason"]="Competing dialogue references remain too close.";Check(group.DisplayStatus=="Subtitle match ambiguous");
   details["text_supported"]=true;details["reason"]="Only part of a provider episode is represented.";Check(group.DisplayStatus=="Partial subtitle correspondence");
   group.Status="unresolved_conflict";Check(group.DisplayStatus=="Unresolved"&&EvidenceReview.Create(group).Assessment.Contains("conflict"));
  });
 }
}
