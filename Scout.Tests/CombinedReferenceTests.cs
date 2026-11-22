using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;
using Scout.Core.Subtitles;

static class CombinedReferenceTests {
 static void Check(bool ok,string message="Combined reference regression"){if(!ok)throw new Exception(message);}
 sealed class Source(SubtitleReferenceSet set):ISubtitleReferenceSource {public Task<SubtitleReferenceSet> LoadAsync(SeriesIdentity s,IReadOnlyList<Episode> c,IProgress<string> p,CancellationToken ct)=>Task.FromResult(set);}
 sealed class Progress:IProgress<string>{public void Report(string s){}}
 public static void Run(Action<string,Action> test,string temp) {
  var identity=new SeriesIdentity("tmdb","5371","iCarly");
  Episode[] catalog=[new(81,3,8,"iQuit iCarly (1)"),new(82,3,9,"iQuit iCarly (2)")];
  var cues=Enumerable.Range(0,240).Select(i=>new Cue(i*10,$"alpha{i} bravo{i} charlie{i} delta{i} echo{i} foxtrot{i}")).ToList();
  SubtitleReference Reference(string id,string name,List<Cue>? text=null)=>new(id,name,"fixture","archive",2,text??cues);
  SubtitleAttempt Match(string key,List<Cue> text,params SubtitleReference[] refs)=>new SubtitleMatcher(new Source(new(identity,refs.ToList())),Path.Combine(temp,"combined-regressions",key))
   .IdentifyAsync(text,catalog,identity,new Progress(),default).GetAwaiter().GetResult();
  var whole=Reference("whole","iCarly.S02E36.iQuit.iCarly.WEBRip.srt");
  test("Exact whole-story title maps alternate release numbering to the full provider range",()=>{
   var result=Match("whole",cues,whole);Check(result.Result!=null);
   var row=SubtitleResult.Row(cues,catalog,result);Check(SubtitleResult.Valid(row,catalog,identity)&&MultipartEvidence.Combined(row));
   Check(result.Details.Array("selected_episodes").Select(e=>e.Int("number")).SequenceEqual(new[]{8,9}));
  });
  test("Equivalent full subtitle versions support the same multipart identity",()=>{
   var alternate=Reference("alternate","iCarly.S03E08E09.WEBRip.srt",cues.Select((c,i)=>i%20==0?c with{Text=c.Text+" alternate"}:c).ToList());
   Check(Match("versions",cues,whole,alternate).Result!=null);
  });
  test("Number-only ranges and malformed or incomplete explicit ranges cannot establish identity",()=>{
   foreach(var name in new[]{"iCarly.S03E08E09.WEBRip.srt","iCarly.S03E08E10.iQuit.iCarly.WEBRip.srt","iCarly.S03E08E09E10.iQuit.iCarly.WEBRip.srt","iCarly.S02E36.iQuit.iCarly.Part.3.WEBRip.srt"})
    Check(TitleMapping.Find(name,catalog,"ref")==null,name);
   var part=TitleMapping.Find("iCarly.S02E36.iQuit.iCarly.Part.1.WEBRip.srt",catalog,"ref");Check(part?.Episodes.Length==1&&part.Episodes[0].Id==81);
   var ambiguous=catalog.Concat([new Episode(91,4,1,"iQuit iCarly (1)"),new Episode(92,4,2,"iQuit iCarly (2)")]).ToArray();
   Check(TitleMapping.Find(whole.Name,ambiguous,"ref")==null);
  });
  test("Half a recording cannot inherit the full story through a numbered subtitle bridge",()=>{
   var half=cues.Take(130).ToList();var numberOnly=Reference("half","iCarly.S03E08E09.WEBRip.srt",half);
   Check(Match("half",half,whole,numberOnly).Result==null);
   Check(Match("partial-source",half,whole).Result==null);
  });
  test("Genuinely conflicting titled dialogue remains unresolved",()=>{
   var other=catalog.Concat([new Episode(90,1,1,"Other Story")]).ToArray();
   var matcher=new SubtitleMatcher(new Source(new(identity,[whole,Reference("conflict","Show.S01E01.Other.Story.WEBRip.srt")])),Path.Combine(temp,"combined-conflict"));
   Check(matcher.IdentifyAsync(cues,other,identity,new Progress(),default).GetAwaiter().GetResult().Result==null);
  });
  test("Fresh unresolved subtitle comparison shows its real reason without a legacy warning",()=>{
   var attempt=Match("held",cues,Reference("numbered","iCarly.S03E08E09.WEBRip.srt"));
   var row=SubtitleResult.Row(cues,catalog,attempt);row["path"]=Path.Combine(temp,"held","long.mkv");row["duration"]=2800.0;
   var report=new JsonObject{["files"]=new JsonArray(row)};
   var item=PreviewPlanner.Create(report,Path.Combine(temp,"held"),"iCarly",catalog,checkSources:false).Items.Single();
   Check(item.Action=="hold"&&item.Reason.Contains("mapping")&&!item.Notes.Any(n=>n.Contains("Legacy")||n.Contains("No current dialogue")));
   row["dialogue"]!["cues"]![0]!["text"]="changed recording";Check(!SubtitleResult.HasComparison(row));
  });
  test("Old cached multipart rejection is recalculated with the new mapping revision",()=>{
   var root=Path.Combine(temp,"combined-cache");var refs=new List<SubtitleReference>{whole};
   var current=identity.Key+":"+SubtitleResult.CatalogHash(catalog)+":"+CollectionResolver.Hash(new JsonArray().ToJsonString());
   var referenceHash=CollectionResolver.Hash(JsonSerializer.Serialize(refs.OrderBy(r=>r.Id),Data.Options));
   var key=CollectionResolver.Hash(JsonSerializer.Serialize(new{SubtitleResult.Version,comparisonRevision=TranscriptComparison.Revision,multipartRevision=3,current,referenceHash,cues},Data.Options));
   Data.Save(Path.Combine(root,"decisions",key+".json"),new{details=new{reason="old rejection"},result=(object?)null});
   var matcher=new SubtitleMatcher(new Source(new(identity,refs)),root);
   Check(matcher.IdentifyAsync(cues,catalog,identity,new Progress(),default).GetAwaiter().GetResult().Result!=null);
  });
 }
}
