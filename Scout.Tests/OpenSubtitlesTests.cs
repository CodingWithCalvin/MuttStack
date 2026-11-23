using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;
using Scout.Core.Subtitles;

static class OpenSubtitlesTests {
 static readonly SeriesIdentity Series=new("tmdb","123","Example");
 static readonly Episode[] Guide=[new(101,2,1,"Silver Comet","The captain finds a silver comet."),new(102,2,2,"Golden River","The captain sails on a golden river.")];
 static void Check(bool ok,string message="OpenSubtitles regression"){if(!ok)throw new Exception(message);}
 static void Reject(Action action){try{action();}catch(Exception ex) when(ex is ArgumentException or InvalidDataException or OpenSubtitlesAccessException){return;}throw new Exception("Expected rejected provider response");}
 static JsonObject Entry(int id=1001,int episode=101,string title="Silver Comet",int parent=123)=>new(){["id"]=id.ToString(),["attributes"]=new JsonObject{
  ["language"]="en",["foreign_parts_only"]=false,["ai_translated"]=false,["machine_translated"]=false,["release"]=$"Example.S02E07.{title}",
  ["feature_details"]=new JsonObject{["feature_type"]="Episode",["parent_tmdb_id"]=parent,["parent_imdb_id"]=999,["tmdb_id"]=episode,["season_number"]=2,["episode_number"]=7,["title"]=title},
  ["files"]=new JsonArray(new JsonObject{["file_id"]=id,["cd_number"]=1,["file_name"]=$"Example.S02E07.{title}.srt"})}};
 static JsonObject Page(params JsonObject[] entries)=>new(){["page"]=1,["total_pages"]=1,["data"]=new JsonArray(entries.Select(e=>(JsonNode)e).ToArray())};
 static List<Cue> Cues(string tag)=>Enumerable.Range(0,180).Select(i=>new Cue(i*5,$"{tag}{i} captain{i} planet{i} nebula{i} {tag}end{i}")).ToList();
 static string Srt(string tag)=>string.Join("\n\n",Cues(tag).Select((c,i)=>$"{i+1}\n{TimeSpan.FromSeconds(c.Start):hh\\:mm\\:ss},000 --> {TimeSpan.FromSeconds(c.Start+2):hh\\:mm\\:ss},000\n{c.Text}"));
 static HttpResponseMessage Json(JsonObject body)=>new(HttpStatusCode.OK){Content=new StringContent(body.ToJsonString())};
 sealed class Progress:IProgress<string>{public List<string> Lines=[];public void Report(string value)=>Lines.Add(value);}
 sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler {
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>send(request,ct);
 }
 sealed class Fixture(string root) {
  public readonly List<(string Method,Uri Uri)> Requests=[];public readonly Progress Logs=new();
  public Func<HttpRequestMessage,JsonObject> Search=_=>Page(Entry());
  public Func<HttpRequestMessage,HttpResponseMessage>? Override;
  public int Remaining=10;
  public OpenSubtitlesCredentials Credentials=new("fake-open-key");
  public HttpMessageHandler Handler()=>new Handler(async(r,ct)=>{
   Requests.Add((r.Method.Method,r.RequestUri!));var uri=r.RequestUri!;Check(!uri.Query.Contains("fake-")&&!uri.Query.Contains("password"));
   if(uri.Host is "api.opensubtitles.com" or "vip-api.opensubtitles.com")Check(r.Headers.GetValues("Api-Key").Single()=="fake-open-key"&&r.Headers.UserAgent.ToString().Contains("Scout"));
   else Check(!r.Headers.Contains("Api-Key")&&r.Headers.Authorization==null,"Never forward credentials to subtitle file hosts");
   if(Override?.Invoke(r) is {} overridden)return overridden;
   if(uri.AbsolutePath.EndsWith("/login"))return Json(new(){["base_url"]="vip-api.opensubtitles.com",["token"]="fake-user-token"});
   if(uri.AbsolutePath.EndsWith("/subtitles"))return Json(Search(r));
   if(uri.AbsolutePath.EndsWith("/download")) {
    var data=JsonNode.Parse(await r.Content!.ReadAsStringAsync(ct))!;Check(data.Text("sub_format")=="srt");
    return Json(new(){["link"]="https://dl.opensubtitles.com/download/"+data["file_id"]!.ToString(),["remaining"]=Remaining,["reset_time_utc"]=DateTimeOffset.UtcNow.AddHours(2).ToString("O")});
   }
   return new(HttpStatusCode.OK){Content=new StringContent(Srt(uri.Segments.Last()))};
  });
  public OpenSubtitlesReferences Source()=>new(root,Credentials,Handler);
  public SubtitleReferenceSet Load(IReadOnlyList<SubtitleReference>? existing=null,Episode[]? guide=null)=>Source().LoadAsync(Series,guide??Guide,existing??[],Logs,default).GetAwaiter().GetResult();
 }
 sealed class Source(SubtitleReferenceSet set,Action? call=null):ISubtitleReferenceSource {
  public Task<SubtitleReferenceSet> LoadAsync(SeriesIdentity series,IReadOnlyList<Episode> catalog,IProgress<string> progress,CancellationToken ct){call?.Invoke();return Task.FromResult(set);}
 }
 public static void Run(Action<string,Action> test,string temp) {
  test("OpenSubtitles credentials are encrypted together and blank saves retain both providers",()=>{
   var folder=Path.Combine(temp,"open-credentials");var db=new ScoutDatabase(Path.Combine(temp,"open-credentials.db"));ScoutStorage.Register(folder,db);
   try{var store=new SettingsStore(folder);SubtitleCredentials.SaveSubdl(store,"keep-subdl");OpenSubtitlesCredentials.Save(store,"fake-open-key","fake-user","fake-password");
    OpenSubtitlesCredentials.Save(store,"","","");Check(OpenSubtitlesCredentials.Read(store)==new OpenSubtitlesCredentials("fake-open-key","fake-user","fake-password"));
    Check(!Encoding.UTF8.GetString(db.Read(Path.Combine(folder,"credentials.dpapi"))).Contains("fake-password"));
    Reject(()=>OpenSubtitlesCredentials.Save(store,"bad key","",""));Check(OpenSubtitlesCredentials.Read(store)!.ApiKey=="fake-open-key");
    OpenSubtitlesCredentials.RemoveLogin(store);Check(OpenSubtitlesCredentials.Read(store)==new OpenSubtitlesCredentials("fake-open-key")&&store.ReadKey(SubtitleCredentials.Subdl)=="keep-subdl");
    Reject(()=>new OpenSubtitlesCredentials("key","user","").Validate());Check(!new OpenSubtitlesCredentials("secret").ToString().Contains("secret"));
   }finally{ScoutStorage.Unregister(folder);}
  });
  test("OpenSubtitles downloads verified English series references using API metadata rather than upload numbering",()=>{
   var root=Path.Combine(temp,"open-basic");var f=new Fixture(root);var set=f.Load();Check(set.References.Count==1&&f.Requests.Count==3);
   var reference=set.References.Single();Check(reference.Cues.Count==180&&reference.Url=="https://www.opensubtitles.com/en/subtitles/1001"&&ReferenceTitles.Find(reference,Guide,out _)?.Episodes.Single().Id==101);
   Check(f.Requests[0].Uri.Query.Contains("parent_tmdb_id=123")&&f.Requests[0].Uri.Query.Contains("languages=en")&&f.Requests[0].Uri.Query.Contains("ai_translated=exclude"));
   int calls=f.Requests.Count;Check(f.Load().References.Count==1&&f.Requests.Count==calls);
   Check(new OpenSubtitlesReferences(root,null).LoadAsync(Series,Guide,new Progress(),default).GetAwaiter().GetResult().References.Count==1);
   Check(Directory.EnumerateFiles(root,"*.json",SearchOption.AllDirectories).All(p=>!File.ReadAllText(p).Contains("fake-open-key")&&!File.ReadAllText(p).Contains("/download/")));
  });
  test("OpenSubtitles login uses its returned official VIP host and never forwards user credentials to downloads",()=>{
   var f=new Fixture(Path.Combine(temp,"open-login")){Credentials=new("fake-open-key","fake-user","fake-password")};
   f.Override=r=>{if(r.RequestUri!.Host=="vip-api.opensubtitles.com")Check(r.Headers.Authorization?.Parameter=="fake-user-token");return null!;};
   Check(f.Load().References.Count==1&&f.Requests.Count==4&&f.Requests[1].Uri.Host=="vip-api.opensubtitles.com");
  });
  test("OpenSubtitles rejects redirected credentials, foreign download hosts and untrusted login hosts",()=>{
   foreach(string target in new[]{"http://api.opensubtitles.com/api/v1/subtitles","https://evil.example/api/v1/subtitles","https://api.opensubtitles.com.evil.example/api/v1/subtitles"}) {
    int calls=0;using var api=new OpenSubtitlesApi(new("fake-open-key"),new Handler((_,_)=>{calls++;var r=new HttpResponseMessage(HttpStatusCode.MovedPermanently);r.Headers.Location=new Uri(target);return Task.FromResult(r);}));
    Reject(()=>api.Search("parent_tmdb_id","123",2,1,default).GetAwaiter().GetResult());Check(calls==1);
   }
   using var badLogin=new OpenSubtitlesApi(new("fake-open-key","u","p"),new Handler((_,_)=>Task.FromResult(Json(new(){["token"]="token",["base_url"]="evil.example"}))));Reject(()=>badLogin.Search("parent_tmdb_id","123",2,1,default).GetAwaiter().GetResult());
   int downloads=0;using var badFile=new OpenSubtitlesApi(new("fake-open-key"),new Handler((_,_)=>{downloads++;return Task.FromResult(Json(new(){["link"]="https://evil.example/file.srt"}));}));Reject(()=>badFile.Download(1,default).GetAwaiter().GetResult());Check(downloads==1);
  });
  foreach(string change in new[]{"series","missing-series","movie","episode","conflicting-title","language","ai","foreign","split"})test("OpenSubtitles ignores unsafe reference metadata: "+change,()=>{
   var row=Entry();var a=row["attributes"]!;var f=a["feature_details"]!;
   switch(change){case "series":f["parent_tmdb_id"]=456;break;case "missing-series":f.AsObject().Remove("parent_tmdb_id");break;case "movie":f["feature_type"]="Movie";break;case "episode":f["tmdb_id"]=999;break;case "conflicting-title":f["title"]="Golden River";break;case "language":a["language"]="fr";break;case "ai":a["ai_translated"]=true;break;case "foreign":a["foreign_parts_only"]=true;break;case "split":a.Array("files")[0]!["cd_number"]=2;break;}
   var fixture=new Fixture(Path.Combine(temp,"open-unsafe-"+change)){Search=_=>Page(row)};Check(fixture.Load().References.Count==0&&fixture.Requests.Count==1);
  });
  test("OpenSubtitles follows pages and checks missing seasons before seasons covered by another provider",()=>{
   var f=new Fixture(Path.Combine(temp,"open-pages"));f.Search=r=>{
    if(r.RequestUri!.Query.Contains("season_number=1"))return Page();
    var page=r.RequestUri!.Query.Contains("page=2")?2:1;var data=Page(page==1?Entry():Entry(1002,102,"Golden River"));data["page"]=page;data["total_pages"]=2;return data;
   };
   var existing=new SubtitleReference("subdl","Example.S01E01.srt","https://dl.subdl.com/subtitle/s.zip","s",1,Cues("older"));
   Check(f.Load([existing],[new(1,1,1,"Earlier"),..Guide]).References.Count==2&&f.Requests[0].Uri.Query.Contains("season_number=2"));
   Check(f.Requests.Count(r=>r.Uri.AbsolutePath.EndsWith("/subtitles"))==3);
  });
  test("OpenSubtitles quota exhaustion saves its last reference and pauses further downloads across Scouts",()=>{
   var f=new Fixture(Path.Combine(temp,"open-quota")){Remaining=0,Search=_=>Page(Entry(),Entry(1002,102,"Golden River"))};
   var set=f.Load();Check(set.References.Count==1&&set.Note.Contains("quota")&&f.Requests.Count==3);var before=f.Requests.Count;
   Check(f.Load().References.Count==1&&f.Requests.Count==before);
   var other=f.Source().LoadAsync(new("tmdb","456","Another"),Guide,new Progress(),default).GetAwaiter().GetResult();Check(other.References.Count==0&&f.Requests.Count==before);
  });
  foreach(var status in new[]{HttpStatusCode.Unauthorized,HttpStatusCode.TooManyRequests,HttpStatusCode.NotAcceptable})test("OpenSubtitles stops rejected requests without retry storms: "+status,()=>{
   var f=new Fixture(Path.Combine(temp,"open-rejected-"+status)){Override=_=>new(status){Content=new StringContent("fake-open-key fake-password raw service detail")}};
   var set=f.Load();Check(set.References.Count==0&&f.Requests.Count==1&&!set.Note.Contains("fake-"));f.Load();Check(f.Requests.Count==1);
  });
  test("Malformed subtitle downloads cannot become reference evidence and another release can still be tried",()=>{
   var f=new Fixture(Path.Combine(temp,"open-malformed")){Search=_=>Page(Entry(),Entry(1002)),Override=r=>r.RequestUri!.AbsolutePath.EndsWith("/1001")?new(HttpStatusCode.OK){Content=new StringContent("<html>Not subtitle content</html>")}:null!};
   Check(f.Load().References.Count==1&&f.Requests.Count==5);
  });
  test("OpenSubtitles lookup cancellation propagates without starting another provider",()=>{
   using var cancel=new CancellationTokenSource();int second=0;
   var f=new Fixture(Path.Combine(temp,"open-cancel")){Override=_=>{cancel.Cancel();throw new OperationCanceledException(cancel.Token);}};
   var combined=new CombinedSubtitleReferences(("OpenSubtitles",f.Source()),("later",new Source(new(Series,[]),()=>second++)));
   try{combined.LoadAsync(Series,Guide,new Progress(),cancel.Token).GetAwaiter().GetResult();throw new Exception("Expected cancellation");}catch(OperationCanceledException){}Check(second==0);
  });
  test("Invalid provider identities and malformed response data cannot discard another provider's references",()=>{
   var reference=new SubtitleReference("safe","Example.S02E01.Silver.Comet.srt","https://dl.subdl.com/subtitle/s.zip","s",2,Cues("same"));
   var bad=new Source(new(new("tmdb","456","Wrong series"),[]));
   var set=new CombinedSubtitleReferences(("wrong",bad),("good",new Source(new(Series,[reference])))).LoadAsync(Series,Guide,new Progress(),default).GetAwaiter().GetResult();Check(set.References.Count==1);
   var f=new Fixture(Path.Combine(temp,"open-invalid-response")){Search=_=>new JsonObject{["data"]="invalid"}};
   set=f.Load();Check(set.References.Count==0&&set.Note.Contains("unavailable"));
  });
  test("OpenSubtitles refreshes invalid cache identity and retains cached dialogue during a malformed refresh",()=>{
   var root=Path.Combine(temp,"open-invalid-cache");var f=new Fixture(root);Check(f.Load().References.Count==1);
   var path=Directory.EnumerateFiles(root,"manifest.json",SearchOption.AllDirectories).Single();var saved=Data.Read(path);saved["series"]!["id"]="456";Data.Save(path,saved);
   Check(f.Load().References.Count==1&&f.Requests.Count==6);
   saved=Data.Read(path);saved["complete"]=false;Data.Save(path,saved);f.Search=_=>new JsonObject{["data"]=null};
   var set=f.Load();Check(set.References.Count==1&&set.Note.Contains("unavailable")&&f.Requests.Count==7);
  });
  test("Provider failure leaves the other provider usable and duplicate/conflicting identities retain their safety checks",()=>{
   var cues=Cues("same");SubtitleReference Ref(string id,string title)=>new(id,$"Example.S02E07.{title}.srt","https://www.opensubtitles.com/en/subtitles/1",id,2,cues);
   foreach(bool conflict in new[]{false,true}) {
    var source=new CombinedSubtitleReferences(("SubDL",new Source(new(Series,[Ref("a","Silver Comet")]))),("OpenSubtitles",new Source(new(Series,[Ref("b",conflict?"Golden River":"Silver Comet")]))));
    var attempt=new SubtitleMatcher(source,Path.Combine(temp,"open-duplicates-"+conflict)).IdentifyAsync(cues,Guide,Series,new Progress(),default).GetAwaiter().GetResult();Check((attempt.Result==null)==conflict);
   }
   var combined=new CombinedSubtitleReferences(("SubDL",new Source(new(Series,[]),()=>throw new HttpRequestException("Unavailable"))),("OpenSubtitles",new Source(new(Series,[Ref("c","Silver Comet")]))));
   var result=new SubtitleMatcher(combined,Path.Combine(temp,"open-provider-failure")).IdentifyAsync(cues,Guide,Series,new Progress(),default).GetAwaiter().GetResult();Check(result.Result!=null&&result.Details.Text("note").Contains("SubDL could not be checked"));
  });
  foreach(bool ai in new[]{false,true})test("Both subtitle providers finish before AI and OpenSubtitles matches need no model: AI="+ai,()=>{
   var root=Path.Combine(temp,"open-pipeline-"+ai);var media=Path.Combine(root,"media");var output=Path.Combine(root,"output");Directory.CreateDirectory(media);int first=0,models=0;
   var f=new Fixture(Path.Combine(output,"references"));var source=new CombinedSubtitleReferences(("SubDL",new Source(new(Series,[]),()=>first++)),("OpenSubtitles",f.Source()));
   var rows=new List<JsonObject>();
   foreach(var name in new[]{"match.mkv","unresolved.mkv"}) {
    var path=Path.Combine(media,name);File.WriteAllText(path,name);var size=new FileInfo(path).Length;var mtime=PreviewPlanner.MtimeNs(path);var digest=PreviewPlanner.SampledDigest(path);
    rows.Add(new(){["path"]=path,["size"]=size,["source_mtime_ns"]=mtime,["sampled_digest"]=digest,["full_dialogue"]=true,["duration"]=900.0,["candidates"]=new JsonArray(),["dialogue"]=new JsonObject{["cues"]=JsonSerializer.SerializeToNode(Cues(name=="match.mkv"?"1001":"unrelated"),Data.Options)},
     ["primary_fingerprint"]=new JsonObject{["algorithm"]="primary-packets-sha256-v1",["video"]=new string(name=="match.mkv"?'a':'b',64),["audio"]=new string('f',64),["size"]=size,["mtime_ns"]=mtime,["sampled_digest"]=digest}});
   }
   var input=Path.Combine(output,"input.json");var guide=Path.Combine(output,"catalog.json");Data.Save(input,new{files=rows});Data.Save(guide,new{episodes=Guide});
   using var http=new HttpClient(new Handler((_,_)=>{models++;Check(first==1&&f.Requests.Count==3);Check(Data.Read(Path.Combine(output,"report.json")).Array("files").Single(r=>r.Text("path")==rows[0].Text("path")).Array("candidates").Count==1);throw new HttpRequestException("Stop isolated model request");}));
   var pipeline=new MediaPipeline(new DialogueMatcher(http),(_,_,_)=>throw new Exception("No media extraction allowed"),new SubtitleMatcher(source,Path.Combine(output,"decisions")));
   var report=Data.Read(pipeline.RunAsync(new(media,guide,output,ai?"model":"",ai?"http://127.0.0.1:1234/v1/chat/completions":"",DialogueInput:input,SelectedFiles:rows.Select(r=>r.Text("path")).ToArray(),SeriesIdentity:Series,UseAiFallback:ai),new Progress(),default).GetAwaiter().GetResult());
   Check(first==1&&models==(ai?1:0)&&report.Array("files").Single(r=>r.Text("path")==rows[0].Text("path")).Array("candidates").Single()!["episode"].Int("id")==101);
  });
 }
}
