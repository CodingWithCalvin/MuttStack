using System.Text.Json;
using System.Net;
using System.IO.Compression;
using Scout.Core;
using Scout.Core.Subtitles;

static class ReferenceValidationTests {
 sealed class Source(SubtitleReferenceSet value):ISubtitleReferenceSource {
  public Task<SubtitleReferenceSet> LoadAsync(SeriesIdentity s,IReadOnlyList<Episode> c,IProgress<string> p,CancellationToken ct)=>Task.FromResult(value);
 }
 sealed class Progress:IProgress<string>{public void Report(string value){}}
 sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler {
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)=>send(r,ct);
 }
 static void Check(bool condition,string message="Reference validation regression") {if(!condition)throw new Exception(message);}
 public static void Run(Action<string,Action> test,string temp) {
  var series=new SeriesIdentity("tmdb","123","Example");
  foreach(var title in new[]{"iQ","iDo"})test("Unique exact short title maps with full dialogue: "+title,()=>{
   Episode[] catalog=[new(1,1,1,title)];
   var cues=Enumerable.Range(0,180).Select(i=>new Cue(i*5,$"topic{i} person{i} action{i} question{i} answer{i} result{i}")).ToList();
   var name="Example.S03E07."+title+".WEBRip.srt";
   var matcher=new SubtitleMatcher(new Source(new(series,[new("short",name,"fixture","archive",3,cues)])),Path.Combine(temp,"short-title-"+title));
   var attempt=matcher.IdentifyAsync(cues,catalog,series,new Progress(),default).GetAwaiter().GetResult();
   Check(SubtitleResult.Valid(SubtitleResult.Row(cues,catalog,attempt),catalog,series));
   Check(matcher.IdentifyAsync(cues.Take(80).ToList(),catalog,series,new Progress(),default).GetAwaiter().GetResult().Result==null);
   foreach(var wrong in new[]{"i","IQuit","iNotDo","iDoo","iGo",""})Check(TitleMapping.Find("Example.S03E07."+wrong+".WEBRip.srt",catalog,"r")==null);
   Check(TitleMapping.Find(name,[catalog[0],new(2,2,1,title)],"r")==null);
  });
  var guide=Enumerable.Range(1,10).Select(i=>new Episode(i,1,i,"Story "+i,"The friends help Alice, Brenda, Carlos and Daniel plan another event.")).ToArray();
  List<Cue> Cues(int seed,bool names)=>Enumerable.Range(0,180).Select(i=>new Cue(i*5,$"{(names?"Alice Brenda Carlos Daniel":"Xena Yvonne Zorro Merlin")} subject{seed}_{i} action{seed}_{i} result{seed}_{i} end{seed}_{i}")).ToList();
  var references=Enumerable.Range(1,10).Select(i=>new SubtitleReference("r"+i,$"Example.S01E{i:00}.Story.{i}.srt","fixture","archive",1,Cues(i,true))).ToList();
  var bad=new SubtitleReference("wrong","Example.S01E11.WEBRip.srt","fixture","archive",1,Cues(11,false));
  test("A mislabeled content outlier is quarantined using catalog and independent reference evidence",()=>{
   var result=ReferenceValidation.Check([..references,bad],guide);
   Check(result.References.Count==10&&result.Rejected.Single().Id==bad.Id);
   Check(result.Note.Contains("quarantined")&&result.Note.Contains("may belong to another show"));
  });
  test("Sparse evidence and duplicate reference versions cannot convict a content outlier",()=>{
   Check(ReferenceValidation.Check([references[0],bad],guide).Rejected.Count==0);
   Check(ReferenceValidation.Check([..Enumerable.Range(0,20).Select(i=>references[0] with{Id="copy"+i}),bad],guide).Rejected.Count==0);
   Check(ReferenceValidation.Check([..references,bad with{Cues=Cues(11,false).Take(40).ToList()}],guide).Rejected.Count==0);
   Check(ReferenceValidation.Check([..references,bad with{Cues=Cues(11,true)}],guide).Rejected.Count==0);
  });
  test("Malformed and non-English references are excluded before comparison",()=>{
   var invalid=new[]{bad with{Id="negative",Cues=[new(-1,"bad cue")]},bad with{Id="unordered",Cues=[new(20,"later"),new(1,"earlier")]},bad with{Id="empty",Cues=[]},bad with{Id="foreign",Cues=Enumerable.Range(0,80).Select(i=>new Cue(i,"これは英語の字幕ではありません")).ToList()}};
   Check(ReferenceValidation.Check(invalid,guide).Rejected.Count==4);
  });
  test("Even an exact title and full matching dialogue cannot authorize a quarantined reference",()=>{
   var titled=bad with{Name="Example.S01E11.Other.Story.srt"};
   var catalog=guide.Concat([new Episode(11,1,11,"Other Story")]).ToArray();
   var matcher=new SubtitleMatcher(new Source(new(series,[..references,titled])),Path.Combine(temp,"quarantined-match"));
   var result=matcher.IdentifyAsync(bad.Cues,catalog,series,new Progress(),default).GetAwaiter().GetResult();
   Check(result.Result==null&&result.Details.Text("note").Contains("quarantined"));
  });
  test("Legacy cached references are revalidated without credentials or network",()=>{
   var cache=Path.Combine(temp,"legacy-reference-validation");var folder=Path.Combine(cache,"subdl-references-v1",CollectionResolver.Hash(series.Key));
   var all=references.Append(bad).Select(r=>r with{Id=CollectionResolver.Hash(r.Id)}).ToArray();
   foreach(var r in all)Data.Save(Path.Combine(folder,"references",r.Id+".json"),r);
   Data.Save(Path.Combine(folder,"manifest.json"),new{series,retrievalPolicy="packs-and-titled-episodes-v2",complete=true,references=all.Select(r=>r.Id)});
   var result=new SubdlReferences(cache,null).LoadAsync(series,guide,new Progress(),default).GetAwaiter().GetResult();
   Check(result.References.Count==10&&result.Note.Contains("quarantined")&&result.Note.Contains("provider episodes"));
   Check(!File.ReadAllText(Path.Combine(folder,"manifest.json")).Contains("quarantined"),"Read-only validation should not rewrite a legacy cache without a refresh.");
  });
  test("Concurrent Scouts refresh the same series cache once without losing its manifest",()=>{
   async Task Rehearse() {
    var cache=Path.Combine(temp,"concurrent-subtitle-refresh");Episode[] catalog=[new(1,1,1,"Opening")];
    using var bytes=new MemoryStream();
    using(var zip=new ZipArchive(bytes,ZipArchiveMode.Create,true)) {
     using var writer=new StreamWriter(zip.CreateEntry("Example.S01E01.Opening.srt").Open());
     writer.Write(string.Join("\n\n",Enumerable.Range(0,30).Select(i=>$"{i+1}\n00:00:{i:00},000 --> 00:00:{i:00},900\nDistinctive dialogue from this episode number {i}.")));
    }
    var downloaded=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int downloads=0,metadata=0;
    HttpResponseMessage Json(string value)=>new(HttpStatusCode.OK){Content=new StringContent(value)};
    HttpMessageHandler Api()=>new Handler(async(request,ct)=>{
     if(request.RequestUri!.Host=="dl.subdl.com") {
      Interlocked.Increment(ref downloads);downloaded.TrySetResult();await release.Task.WaitAsync(ct);
      return new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes.ToArray())};
     }
     return request.RequestUri.AbsolutePath.EndsWith("movies/search")?Json("""{"results":[{"sd_id":"sd123","imdb_id":"tt999","type":"tv"}]}"""):
      Json("""{"subtitles":[{"lang":"english","url":"/subtitle/shared.zip","unpack_files":["Example.S01E01.Opening.srt"]}]}""");
    });
    HttpMessageHandler Metadata()=>new Handler((_,_)=>{Interlocked.Increment(ref metadata);return Task.FromResult(Json("""{"id":123,"imdb_id":"tt999"}"""));});
    var first=new SubdlReferences(cache,"fixture","fixture",Api,Metadata).LoadAsync(series,catalog,new Progress(),default);
    await downloaded.Task.WaitAsync(TimeSpan.FromSeconds(3));
    var second=new SubdlReferences(cache,"fixture","fixture",Api,Metadata).LoadAsync(series,catalog,new Progress(),default);
    Check(!second.IsCompleted);release.SetResult();var results=await Task.WhenAll(first,second).WaitAsync(TimeSpan.FromSeconds(5));
    Check(downloads==1&&metadata==1&&results.All(r=>r.References.Count==1));
    var manifest=Data.Read(Directory.EnumerateFiles(cache,"manifest.json",SearchOption.AllDirectories).Single());
    Check(manifest.Bool("complete")&&manifest.Array("references").Count==1&&manifest.Int("validation_revision")==ReferenceValidation.Revision);
   }
   Rehearse().GetAwaiter().GetResult();
  });
 }
}
