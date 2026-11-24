using System.IO.Compression;
using System.Net;
using System.Text.Json.Nodes;
using Scout.Core;
using Scout.Core.Subtitles;

static class SubtitleRetrievalTests {
 sealed class Progress:IProgress<string>{public List<string> Lines=[];public void Report(string value)=>Lines.Add(value);}
 sealed class Response(Func<HttpRequestMessage,HttpResponseMessage> respond):HttpMessageHandler {
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(respond(request));
 }
 static void Check(bool condition,string message="Subtitle retrieval regression"){if(!condition)throw new Exception(message);}
 static HttpResponseMessage Json(string body)=>new(HttpStatusCode.OK){Content=new StringContent(body)};
 static HttpResponseMessage Zip(params (string Name,string Tag)[] files) {
  using var bytes=new MemoryStream();
  using(var zip=new ZipArchive(bytes,ZipArchiveMode.Create,true))foreach(var (name,tag) in files) {
   using var writer=new StreamWriter(zip.CreateEntry(name).Open());
   writer.Write(string.Join("\n\n",Enumerable.Range(0,180).Select(i=>$"{i+1}\n{TimeSpan.FromSeconds(i*5):hh\\:mm\\:ss},000 --> {TimeSpan.FromSeconds(i*5+2):hh\\:mm\\:ss},000\n{tag}{i} captain{i} planet{i} nebula{i} {tag}end{i}")));
  }
  return new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes.ToArray())};
 }
 sealed class Fixture(string root,Func<Uri,HttpResponseMessage> respond) {
  public readonly List<Uri> Requests=[];public readonly Progress Logs=new();
  public string Titles="""{"results":[{"sd_id":123,"imdb_id":"tt999","type":"tv"}]}""";
  public SubtitleReferenceSet Load(Episode[] guide)=>new SubdlReferences(root,"fake-retrieval-key","fake-metadata-token",
   ()=>new Response(r=>{var uri=r.RequestUri!;Requests.Add(uri);Check(uri.Host is "api.subdl.com" or "dl.subdl.com");Check(!uri.Query.Contains("fake-"));
    if(uri.AbsolutePath.EndsWith("movies/search"))return Json(Titles);return respond(uri);}),
   ()=>new Response(r=>{Check(r.RequestUri!.Host=="api.themoviedb.org");return Json("""{"id":123,"imdb_id":"tt999"}""");}))
   .LoadAsync(new("tmdb","123","Example"),guide,Logs,default).GetAwaiter().GetResult();
 }
 public static void Run(Action<string,Action> test,string temp) {
  test("Repeated SubDL runs reuse successful searches without spending the network budget or starving later seasons",()=>{
   var f=new Fixture(Path.Combine(temp,"retrieval-budget-cache"),_=>Json("{\"subtitles\":[],\"results\":[]}"));
   var guide=Enumerable.Range(1,4).SelectMany(s=>Enumerable.Range(1,20).Select(e=>new Episode(s*100+e,s,e,$"Story {s}-{e}"))).ToArray();
   Check(f.Load(guide).References.Count==0);int first=f.Requests.Count;Check(first==61);
   Check(f.Load(guide).References.Count==0);int second=f.Requests.Count;Check(second>first&&second-first<=60&&f.Requests.Any(u=>u.Query.Contains("season=4&")));
   f.Load(guide);Check(f.Requests.Count==second&&f.Logs.Lines.Any(l=>l.Contains("cached subtitle search")));
  });
  test("SubDL IDs normalize documented prefixed and numeric identities only",()=>{
   foreach(var raw in new[]{"123","\"123\"","\"sd123\"","\"SD000123\"","\"  sd123  \""})Check(SubdlIdentity.Normalize(JsonNode.Parse(raw))=="123");
   foreach(var raw in new[]{"null","0","-123","123.5","true","{}","[]","\"sd\"","\"sd123x\"","\"123 OR 1\"","\"000\""})Check(SubdlIdentity.Normalize(JsonNode.Parse(raw))==null);
  });
  foreach(var prefixedTitle in new[]{true,false})test($"SubDL title and subtitle IDs may use different representations (prefixed title={prefixedTitle})",()=>{
   var f=new Fixture(Path.Combine(temp,"retrieval-prefixed-"+prefixedTitle),u=>{
    if(u.Host=="dl.subdl.com")return Zip(("Example.S02E01.Silver.Comet.srt","first"));
    Check(u.Query.Contains("sd_id=123&"));
    return Json("{\"results\":[{\"sd_id\":"+(prefixedTitle?"123":"\"SD000123\"")+",\"imdb_id\":\"tt999\",\"tmdb_id\":123,\"type\":\"tv\"}],\"subtitles\":[{\"lang\":\"en\",\"url\":\"/subtitle/one.zip\",\"unpack_files\":[\"Example.S02E01.Silver.Comet.srt\"]}]}");
   });
   f.Titles="{\"results\":[{\"sd_id\":"+(prefixedTitle?"\"sd123\"":"123")+",\"imdb_id\":\"tt999\",\"type\":\"tv\"},{\"sd_id\":\"000123\",\"imdb_id\":\"tt999\",\"type\":\"tv\"}]}";
   var set=f.Load([new(1,2,1,"Silver Comet")]);Check(set.References.Count==1&&!set.Note.Contains("another series")&&f.Requests.Count==3);
  });
  foreach(var identity in new[]{"\"sd_id\":\"sd124\"","\"sd_id\":\"bad123\"","\"sd_id\":123,\"imdb_id\":\"tt111\"","\"sd_id\":123,\"tmdb_id\":456","\"sd_id\":123,\"type\":\"movie\""})test("SubDL normalization retains identity rejection: "+identity,()=>{
   var f=new Fixture(Path.Combine(temp,"retrieval-bad-"+CollectionResolver.Hash(identity)),u=>{Check(u.Host!="dl.subdl.com");return Json("{\"results\":[{"+identity+"}],\"subtitles\":[{\"url\":\"/subtitle/wrong.zip\"}]}");});
   var set=f.Load([new(1,1,1,"Silver Comet")]);Check(set.References.Count==0&&set.Note.Contains("another series"));
  });
  test("SubDL search cannot normalize away a conflicting TMDB title identity",()=>{
   var f=new Fixture(Path.Combine(temp,"retrieval-title-identity"),_=>throw new Exception("No subtitle request permitted"));
   f.Titles="""{"results":[{"sd_id":"sd123","imdb_id":"tt999","tmdb_id":456,"type":"tv"}]}""";
   Check(f.Load([new(1,1,1,"Silver Comet")]).References.Count==0&&f.Requests.Count==1);
  });
  test("Empty English season results fall back to numbered individual episode releases",()=>{
   var cache=Path.Combine(temp,"retrieval-empty-season");Episode[] guide=[new(1,2,1,"Silver Comet"),new(2,2,2,"Golden River")];
   var f=new Fixture(cache,u=>{
    if(u.Host=="dl.subdl.com")return u.AbsolutePath.EndsWith("one.zip")?Zip(("Example.S02E01.srt","first")):Zip(("Example.S02E02.srt","second"));
    Check(u.Query.Contains("season=2&")&&u.Query.Contains("languages=en")&&u.Query.Contains("releases=1"));
    return Json(u.Query.Contains("episode=1")?"""{"results":[{"sd_id":123,"type":"tv","imdb_id":"tt999"}],"subtitles":[{"lang":"en","url":"/subtitle/one.zip","unpack_files":["Example.S02E01.srt"]}]}""":
     u.Query.Contains("episode=2")?"""{"subtitles":[{"lang":"english","url":"/subtitle/two.zip","unpack_files":["Example.S02E02.srt"]}]}""":"""{"subtitles":[]}""");
   });
   var set=f.Load(guide);Check(set.References.Count==2&&set.Note.Contains("02: 2")&&set.Note.Contains("0 of 2 provider episodes"));
   Check(set.References.All(r=>ReferenceTitles.Find(r,guide,out _)==null),"Request numbers must not become provider identities");
   Check(f.Requests.Count==6);int before=f.Requests.Count;Check(f.Load(guide).References.Count==2&&f.Requests.Count==before);
   var manifest=Directory.EnumerateFiles(cache,"manifest.json",SearchOption.AllDirectories).Single();Check(Data.Read(manifest).Bool("complete"));
   Check(Directory.EnumerateFiles(cache,"*.json",SearchOption.AllDirectories).All(p=>!File.ReadAllText(p).Contains("fake-retrieval-key")));
  });
  test("Numbered uploads survive broken largest packs and gone downloads",()=>{
   Episode[] guide=[new(1,1,1,"Silver Comet"),new(2,1,2,"Golden River")];
   var f=new Fixture(Path.Combine(temp,"retrieval-alternates"),u=>{
    if(u.Host=="dl.subdl.com")return u.AbsolutePath switch {
     "/subtitle/broken.zip"=>new(HttpStatusCode.OK){Content=new StringContent("not a zip")},
     "/subtitle/gone.zip"=>new(HttpStatusCode.NotFound),
     "/subtitle/one.zip"=>Zip(("Example.S01E01.srt","first")),
     _=>Zip(("Example.S01E02.srt","second"))};
    return Json("""{"subtitles":[{"lang":"en","url":"/subtitle/broken.zip","unpack_files":["a.srt","b.srt","c.srt"]},{"lang":"en","url":"/subtitle/gone.zip","unpack_files":["a.srt","b.srt"]},{"lang":"en","url":"/subtitle/one.zip","unpack_files":["Example.S01E01.srt"]},{"lang":"en","url":"/subtitle/two.zip","unpack_files":["Example.S01E02.srt"]}]}""");
   });
   var set=f.Load(guide);Check(set.References.Count==2&&f.Requests.Count(u=>u.Host=="dl.subdl.com")==4);
   Check(set.Note.Contains("unusable subtitle archive")&&set.Note.Contains("unavailable subtitle download"));
  });
  test("Per-file release titles map provider episodes without spreading a season pack title",()=>{
   Episode[] guide=[new(1,1,1,"Silver Comet"),new(2,1,2,"Golden River")];
   var f=new Fixture(Path.Combine(temp,"retrieval-release-titles"),u=>u.Host=="dl.subdl.com"?
    Zip(("folder/Example.S01E07.srt","first"),("Example.S01E08.srt","second"),("Example.S01E09.srt","third")):
    Json("""{"subtitles":[{"lang":"en","url":"/subtitle/titles.zip","release_name":"Example.S01E01.Silver.Comet","unpack_files":[{"name":"folder/Example.S01E07.srt","release_name":"Example.S01E07.Silver.Comet","language":"en"},{"name":"Example.S01E08.srt","release_name":"Example.S01E08.Golden.River","language":"en"},{"name":"Example.S01E09.srt","language":"en"}]}]}"""));
   var set=f.Load(guide);Check(set.References.Count==3);
   Check(ReferenceTitles.Find(set.References[0],guide,out _)?.Episodes.Single().Id==1);
   Check(ReferenceTitles.Find(set.References[1],guide,out _)?.Episodes.Single().Id==2);
   Check(ReferenceTitles.Find(set.References[2],guide,out _)==null&&set.Note.Contains("2 of 2 provider episodes"));
   Check(!f.Requests.Any(u=>u.Query.Contains("episode=")));
  });
  test("Explicit wrong-series search results cannot import a subtitle archive",()=>{
   var f=new Fixture(Path.Combine(temp,"retrieval-wrong-series"),u=>{
    Check(u.Host!="dl.subdl.com");return Json("""{"results":[{"sd_id":456,"imdb_id":"tt456","type":"tv"}],"subtitles":[{"url":"/subtitle/wrong.zip"}]}""");
   });
   var set=f.Load([new(1,1,1,"Silver Comet")]);Check(set.References.Count==0&&set.Note.Contains("another series"));
  });
  foreach(var status in new[]{HttpStatusCode.Unauthorized,HttpStatusCode.TooManyRequests})test($"SubDL {status} stops fallback requests and preserves downloaded references",()=>{
   var cache=Path.Combine(temp,"retrieval-limit-"+status);int downloads=0;
   var f=new Fixture(cache,u=>{
    if(u.Host=="dl.subdl.com")return ++downloads==1?Zip(("Example.S01E01.Silver.Comet.srt","first")):new(status);
    return Json("""{"subtitles":[{"url":"/subtitle/one.zip","unpack_files":["Example.S01E01.Silver.Comet.srt"]},{"url":"/subtitle/two.zip","unpack_files":["Example.S01E02.Golden.River.srt"]},{"url":"/subtitle/three.zip","unpack_files":["Example.S01E03.srt"]}]}""");
   });
   var guide=new[]{new Episode(1,1,1,"Silver Comet"),new Episode(2,1,2,"Golden River")};var set=f.Load(guide);
   Check(set.References.Count==1&&downloads==2&&f.Requests.Count==4);
   Check(!Data.Read(Directory.EnumerateFiles(cache,"manifest.json",SearchOption.AllDirectories).Single()).Bool("complete"));
   var offline=new SubdlReferences(cache,null).LoadAsync(set.Series,guide,f.Logs,default).GetAwaiter().GetResult();Check(offline.References.Count==1);
  });
  test("Duplicate dialogue under different numbers does not conceal missing references",()=>{
   var cache=Path.Combine(temp,"retrieval-duplicate-labels");Episode[] guide=[new(1,1,1,"Silver Comet"),new(2,1,2,"Golden River")];
   var f=new Fixture(cache,u=>u.Host=="dl.subdl.com"?Zip(("Example.S01E01.srt","same"),("Example.S01E02.srt","same")):
    Json("""{"subtitles":[{"url":"/subtitle/duplicates.zip","unpack_files":["Example.S01E01.srt","Example.S01E02.srt"]}]}"""));
   var set=f.Load(guide);Check(set.References.Count==2&&f.Requests.Count(u=>u.Host=="dl.subdl.com")==1);
   Check(f.Requests.Any(u=>u.Query.Contains("episode=1"))&&f.Requests.Any(u=>u.Query.Contains("episode=2")));
   Check(!Data.Read(Directory.EnumerateFiles(cache,"manifest.json",SearchOption.AllDirectories).Single()).Bool("complete"));
  });
  test("Legacy complete caches refresh missing episode coverage without redownloading saved packs",()=>{
   var cache=Path.Combine(temp,"retrieval-legacy");Episode[] guide=[new(1,1,1,"Silver Comet"),new(2,1,2,"Golden River")];bool repaired=false;
   var f=new Fixture(cache,u=>{
    if(u.Host=="dl.subdl.com")return u.AbsolutePath.EndsWith("one.zip")?Zip(("Example.S01E01.Silver.Comet.srt","first")):Zip(("Example.S01E02.Golden.River.srt","second"));
    return Json(repaired&&u.Query.Contains("episode=2")?"""{"subtitles":[{"url":"/subtitle/two.zip","unpack_files":["Example.S01E02.Golden.River.srt"]}]}""":"""{"subtitles":[{"url":"/subtitle/one.zip","unpack_files":["Example.S01E01.Silver.Comet.srt"]}]}""");
   });
   Check(f.Load(guide).References.Count==1);var manifest=Directory.EnumerateFiles(cache,"manifest.json",SearchOption.AllDirectories).Single();var legacy=Data.Read(manifest);legacy["complete"]=true;legacy["retrieval_policy"]="legacy-largest-pack";Data.Save(manifest,legacy);
   foreach(var search in Directory.EnumerateFiles(cache,"*.json",SearchOption.AllDirectories).Where(p=>p.Contains("searches-v1"))){var entry=Data.Read(search);entry["updated"]=DateTimeOffset.UtcNow.AddDays(-2).ToString("O");Data.Save(search,entry);}
   repaired=true;Check(f.Load(guide).References.Count==2&&Data.Read(manifest).Bool("complete"));
   Check(f.Requests.Count(u=>u.Host=="dl.subdl.com"&&u.AbsolutePath.EndsWith("one.zip"))==1);
  });
  test("Episode fallback stops redundant queries when one response fills the season",()=>{
   var f=new Fixture(Path.Combine(temp,"retrieval-whole-season-fallback"),u=>{
    if(u.Host=="dl.subdl.com")return Zip(("Example.S02E01.Silver.Comet.srt","first"),("Example.S02E02.Golden.River.srt","second"));
    Check(!u.Query.Contains("episode=2"));
    return Json(u.Query.Contains("episode=1")?"""{"subtitles":[{"url":"/subtitle/whole.zip","unpack_files":["Example.S02E01.Silver.Comet.srt","Example.S02E02.Golden.River.srt"]}]}""":"""{"subtitles":[]}""");
   });
   Check(f.Load([new(1,2,1,"Silver Comet"),new(2,2,2,"Golden River")]).References.Count==2&&f.Requests.Count==4);
  });
  test("Subtitle archive downloads obey the bounded budget",()=>{
   var f=new Fixture(Path.Combine(temp,"retrieval-download-budget"),u=>{
    if(u.Host=="dl.subdl.com")return Zip(("Example.S01E01.Silver.Comet.srt",u.AbsolutePath));
    var rows=new JsonArray(Enumerable.Range(1,60).Select(i=>(JsonNode)new JsonObject{["url"]=$"/subtitle/{i:00}.zip",["unpack_files"]=new JsonArray("Example.S01E01.Silver.Comet.srt")}).ToArray());
    return Json(new JsonObject{["subtitles"]=rows}.ToJsonString());
   });
   var set=f.Load([new(1,1,1,"Silver Comet"),new(2,1,2,"Golden River")]);
   Check(f.Requests.Count(u=>u.Host=="dl.subdl.com")==48&&set.Note.Contains("retrieval limit"));
  });
  test("Incomplete subtitle catalogs obey a bounded search budget",()=>{
   var cache=Path.Combine(temp,"retrieval-search-budget");var f=new Fixture(cache,_=>Json("""{"subtitles":[]}"""));
   var set=f.Load(Enumerable.Range(1,100).Select(i=>new Episode(i,1,i,"Unlisted Episode "+i)).ToArray());
   Check(f.Requests.Count(u=>u.AbsolutePath.EndsWith("subtitles/search"))==60&&set.Note.Contains("retrieval limit"));
   Check(!Data.Read(Directory.EnumerateFiles(cache,"manifest.json",SearchOption.AllDirectories).Single()).Bool("complete"));
  });
 }
}
