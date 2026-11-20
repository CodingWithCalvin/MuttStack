using System.IO.Compression;
using System.Net;
using System.Text.Json.Nodes;
using Scout.Core;
using Scout.Core.Subtitles;

static class SubtitleCoverageTests {
 sealed class Progress:IProgress<string>{public List<string> Lines=[];public void Report(string value)=>Lines.Add(value);}
 sealed class Response(Func<HttpRequestMessage,HttpResponseMessage> respond):HttpMessageHandler {
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)=>Task.FromResult(respond(r));
 }
 public static void Run(Action<string,Action> test,string temp) {
  void Check(bool value,string message="Subtitle coverage assertion failed"){if(!value)throw new Exception(message);}
  foreach(bool combined in new[]{true,false})test(combined?"Multi-season English packs report actual coverage on first load and cache reuse":"Genuine English reference gaps remain visible and do not receive the complete-cache TTL",()=>{
   var cache=Path.Combine(temp,"subtitle-coverage-"+combined);var series=new SeriesIdentity("tvdb","123","Example");
   Episode[] guide=[new(1,1,1,"Opening"),new(2,2,1,"Return")];
   using var bytes=new MemoryStream();
   using(var zip=new ZipArchive(bytes,ZipArchiveMode.Create,true))foreach(var name in combined?new[]{"Example.S01E01.Opening.srt","Example.S02E01.Return.srt"}:new[]{"Example.S01E01.Opening.srt"}) {
    using var writer=new StreamWriter(zip.CreateEntry(name).Open());writer.Write(string.Join("\n\n",Enumerable.Range(0,30).Select(i=>$"{i+1}\n00:00:{i:00},000 --> 00:00:{i:00},900\nA distinctive line of dialogue number {i}.")));
   }
   int requests=0;
   HttpResponseMessage Json(string value)=>new(HttpStatusCode.OK){Content=new StringContent(value)};
   HttpMessageHandler Api()=>new Response(r=>{
    requests++;var url=r.RequestUri!;Check(url.Host is "api.subdl.com" or "dl.subdl.com");
    if(url.Host=="dl.subdl.com")return new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes.ToArray())};
    if(url.AbsolutePath.EndsWith("movies/search"))return Json("""{"results":[{"sd_id":"sd123","imdb_id":"tt999","type":"tv"}]}""");
    Check(url.Query.Contains("languages=en"));
    return Json(url.Query.Contains("season=2&")?"""{"subtitles":[{"lang":"french","url":"/subtitle/french.zip"}]}""":"""{"subtitles":[{"lang":"english","url":"/subtitle/combined.zip","unpack_files":["Example.S01E01.Opening.srt","Example.S02E01.Return.srt"]}]}""");
   });
   HttpMessageHandler Metadata()=>new Response(_=>Json("""{"id":4,"externals":{"thetvdb":123,"imdb":"tt999"}}"""));
   var source=new SubdlReferences(cache,"fake-coverage-key",apiHandler:Api,metadataHandler:Metadata);var logs=new Progress();
   var first=source.LoadAsync(series,guide,logs,default).GetAwaiter().GetResult();
   Check(first.References.Count==(combined?2:1)&&!logs.Lines.Any(l=>l.Contains("No English reference pack")));
   Check(first.Note.Contains(combined?"02: 1":"No English reference files loaded for subtitle season(s) 02"));
   var manifest=Directory.EnumerateFiles(cache,"manifest.json",SearchOption.AllDirectories).Single();Check(Data.Read(manifest).Bool("complete")==combined);
   if(!combined){var legacy=Data.Read(manifest);legacy["complete"]=true;Data.Save(manifest,legacy);}
   int before=requests;var next=source.LoadAsync(series,guide,logs,default).GetAwaiter().GetResult();
   Check(combined?requests==before:requests>before);
   Check(next.Note.Contains(combined?"02: 1":"No English reference files loaded for subtitle season(s) 02"));
   Check(!File.ReadAllText(manifest).Contains("fake-coverage-key"));
  });
 }
}
