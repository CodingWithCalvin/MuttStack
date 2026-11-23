using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;
using Scout.Core.Subtitles;

static class SubtitleNormalizationTests {
 sealed class Progress:IProgress<string>{public void Report(string s){}}
 sealed class Source(SubtitleReferenceSet set):ISubtitleReferenceSource{public Task<SubtitleReferenceSet> LoadAsync(SeriesIdentity s,IReadOnlyList<Episode> e,IProgress<string> p,CancellationToken ct)=>Task.FromResult(set);}
 sealed class Response(Func<HttpRequestMessage,HttpResponseMessage> handle):HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)=>Task.FromResult(handle(r));}
 static void Check(bool ok,string message="Subtitle normalization assertion failed"){if(!ok)throw new Exception(message);}
 public static void Run(Action<string,Action> test,string temp) {
  test("Rolling caption updates normalize without changing stored cues or lowering match thresholds",()=>{
   var words=Enumerable.Range(0,191).Select(i=>"distinct"+i).ToArray();
   var rolling=Enumerable.Range(0,180).Select(i=>new Cue(i*2,string.Join(" ",words.Skip(i).Take(12)))).ToList();
   var normal=Enumerable.Range(0,16).Select(i=>new Cue(i*24,string.Join(" ",words.Skip(i*12).Take(12)))).ToList();
   var before=JsonSerializer.Serialize(rolling);var raw=TranscriptComparison.Compare(TranscriptComparison.Index(normal),TranscriptComparison.Index(rolling.Select(c=>c.Text)),[]);
   Check(raw.ReferenceCoverage<.45&&TranscriptComparison.Index(rolling).Words.SequenceEqual(words));
   var series=new SeriesIdentity("tvdb","1","Example");Episode[] guide=[new(1,1,1,"Silver Comet")];
   var references=new SubtitleReferenceSet(series,[new("rolling","Example.S01E01.Silver.Comet.srt","https://example.test/rolling.zip","archive",1,rolling)]);
   var matcher=new SubtitleMatcher(new Source(references),Path.Combine(temp,"rolling-captions"));var match=matcher.IdentifyAsync(normal,guide,series,new Progress(),default).GetAwaiter().GetResult();
   Check(match.Result!=null&&SubtitleResult.Valid(SubtitleResult.Row(normal,guide,match),guide,series)&&JsonSerializer.Serialize(rolling)==before);
   var partial=matcher.IdentifyAsync(normal.Take(5).ToList(),guide,series,new Progress(),default).GetAwaiter().GetResult();Check(partial.Result==null,"An excerpt cannot become a whole-episode assignment");
   var bad=new SubtitleMatcher(new Source(new(series,[references.References[0] with{Name="Example.S01E01.srt"}])),Path.Combine(temp,"rolling-unmapped"));
   var unmapped=bad.IdentifyAsync(normal,guide,series,new Progress(),default).GetAwaiter().GetResult();Check(unmapped.Result==null&&unmapped.Details.Text("reason").Contains("mapping"));
   var summary=SubtitleResult.FallbackSummary(unmapped.Details);Check(summary.Contains("Example.S01E01.srt")&&summary.Contains("no verified provider-title mapping"));
  });
  test("Isolated echoes, repeated sentences, and distant overlap remain in dialogue",()=>{
   Cue[][] cases=[
    [new(0,"please come back to the house"),new(2,"please come back to the house"),new(4,"please come back to the house")],
    [new(0,"alpha beta gamma delta epsilon"),new(2,"gamma delta epsilon zeta eta"),new(4,"unrelated speech follows this exchange")],
    [new(0,"alpha beta gamma delta epsilon"),new(30,"gamma delta epsilon zeta eta"),new(60,"epsilon zeta eta theta iota")]
   ];
   foreach(var cues in cases)Check(TranscriptComparison.Index(cues).Words.SequenceEqual(TranscriptComparison.Index(cues.Select(c=>c.Text)).Words));
  });
  test("Title mapping recognizes x numbering without accepting number-only or conflicting provider titles",()=>{
   Episode[] guide=[new(1,6,1,"iLost My Mind"),new(2,6,2,"iDate Freddie and Sam")];
   Check(TitleMapping.Find("iCarly - 05x01 - iLost My Mind.srt",guide,"id")?.Episodes.Single().Id==1);
   Check(TitleMapping.Find("iCarly - 05x01 - Unrelated Title.srt",guide,"id")==null&&TitleMapping.Find("iCarly - 05x01 - WEBRip.srt",guide,"id")==null);
  });
  test("Reference retrieval includes titled episode uploads and refreshes old season-only caches",()=>{
   var root=Path.Combine(temp,"subtitle-episode-uploads");var series=new SeriesIdentity("tvdb","1","Example");Episode[] guide=[new(1,1,1,"First Story"),new(2,1,2,"Second Story"),new(3,1,3,"Third Story")];
   byte[] Zip(IEnumerable<string> names){using var bytes=new MemoryStream();using(var z=new ZipArchive(bytes,ZipArchiveMode.Create,true))foreach(var name in names){using var w=new StreamWriter(z.CreateEntry(name).Open());w.Write(string.Join("\n\n",Enumerable.Range(0,30).Select(i=>$"{i+1}\n00:00:{i:00},000 --> 00:00:{i:00},900\nstory{TitleMapping.Label(name)!.First}cue{i} alpha{i} planet{i} nebula{i} captain{i} finale{i}.")));}return bytes.ToArray();}
   var uploads=guide.Select(e=>$"Example.01x{e.Number:00}.{e.Title}.srt").ToArray();var downloads=new List<string>();int searches=0;
   HttpResponseMessage Json(string s)=>new(HttpStatusCode.OK){Content=new StringContent(s)};
   HttpMessageHandler Api()=>new Response(r=>{
    var uri=r.RequestUri!;
    if(uri.Host=="dl.subdl.com") {downloads.Add(uri.AbsolutePath);return new(HttpStatusCode.OK){Content=new ByteArrayContent(uri.AbsolutePath.EndsWith("pack.zip")?Zip(guide.Select(e=>$"Example.S01E{e.Number:00}.srt")):Zip([uploads[int.Parse(Path.GetFileNameWithoutExtension(uri.AbsolutePath))-1]]))};}
    if(uri.AbsolutePath.EndsWith("movies/search"))return Json("""{"results":[{"sd_id":"sd1","imdb_id":"tt1","type":"tv"}]}""");
    searches++;Check(!uri.Query.Contains("full_season=")&&uri.Query.Contains("languages=en"));
    return Json(JsonSerializer.Serialize(new{subtitles=new[]{new{lang="english",url="/subtitle/pack.zip",unpack_files=guide.Select(e=>$"Example.S01E{e.Number:00}.srt").ToArray()}}.Concat(guide.Select(e=>new{lang="english",url=$"/subtitle/{e.Number}.zip",unpack_files=new[]{uploads[e.Number-1]}}))}));
   });
   HttpMessageHandler Metadata()=>new Response(_=>Json("""{"id":9,"externals":{"thetvdb":1,"imdb":"tt1"}}"""));
   var source=new SubdlReferences(root,"fake-reference-key",apiHandler:Api,metadataHandler:Metadata);var set=source.LoadAsync(series,guide,new Progress(),default).GetAwaiter().GetResult();
   Check(set.References.Count==6&&downloads.Count==4&&uploads.All(n=>set.References.Any(r=>r.Name==n)));
   var manifest=Directory.GetFiles(root,"manifest.json",SearchOption.AllDirectories).Single();var old=Data.Read(manifest);old.Remove("retrieval_policy");Data.Save(manifest,old);
   source.LoadAsync(series,guide,new Progress(),default).GetAwaiter().GetResult();Check(searches==2&&downloads.Count==4);
   source.LoadAsync(series,guide,new Progress(),default).GetAwaiter().GetResult();Check(searches==2,"Fresh retrieval policy should reuse its saved cache");
  });
  if(Environment.GetEnvironmentVariable("SCOUT_SUBTITLE_REPLAY") is string fixture)test("Isolated iCarly replay recovers six speech matches and keeps unmapped or wrong references held",()=>{
   var report=Data.Read(Path.Combine(fixture,"6e5bdca7626b4fde987a2b0ff343c39a-report.json"));var series=report["series_identity"]!.Deserialize<SeriesIdentity>(Data.Options)!;var guide=Data.Catalog(Path.Combine(fixture,"catalog.json"));
   var refs=JsonSerializer.Deserialize<List<SubtitleReference>>(File.ReadAllText(Path.Combine(fixture,"enriched-references.json")),Data.Options)!;
   var matcher=new SubtitleMatcher(new Source(new(series,refs)),Path.Combine(temp,"icarly-replay"));var accepted=new List<string>();int mappingHeld=0,otherHeld=0;
   foreach(var row in report.Array("files").OfType<JsonObject>().Where(r=>r["transcription"]!=null)) {
    var cues=MultipartEvidence.Cues(row);var result=matcher.IdentifyAsync(cues,guide,series,new Progress(),default).GetAwaiter().GetResult();
    if(result.Result!=null){Check(SubtitleResult.Valid(SubtitleResult.Row(cues,guide,result),guide,series));accepted.Add(result.Details.Array("selected_episodes").Single().Text("title"));}
    else if(result.Details.Text("reason").Contains("mapping"))mappingHeld++;else otherHeld++;
   }
   Check(accepted.Order().SequenceEqual(new[]{"iGo One Direction","iOpen a Restaurant","iApril Fools","iPear Store","iHalfoween","iBattle Chip"}.Order())&&mappingHeld==6&&otherHeld==2);
  });
 }
}
