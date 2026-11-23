using Scout.Core;
using Scout.Core.Subtitles;
using System.Text.Json;
using System.Text.Json.Nodes;
static class TitleVariationTests {
 static void Check(bool value){if(!value)throw new Exception("Title variation regression");}
 sealed class Source(SubtitleReferenceSet set):ISubtitleReferenceSource {public Task<SubtitleReferenceSet> LoadAsync(SeriesIdentity s,IReadOnlyList<Episode> c,IProgress<string> p,CancellationToken ct)=>Task.FromResult(set);}
 sealed class Progress:IProgress<string>{public void Report(string value){}}
 public static void Run(Action<string,Action> test,string temp) {
  var identity=new SeriesIdentity("tmdb","5371","iCarly");
  Episode[] guide=[new(1044091,1,9,"iWant to Date Freddie")];
  const string name="iCarly.S01E08.iWill.Date.Freddie.WEBRip.Netflix.en[cc].srt";
  test("Distinctive title wording survives alternate first-person phrasing and release numbering",()=>{
   var link=TitleMapping.Find(name,guide,"ref");
   Check(link?.Complete==true&&link.Episodes.Single().Id==1044091&&link.Method=="unique-reference-title-phrase-variant");
  });
  test("Title variation preserves negation, names and actions and rejects short or ambiguous titles",()=>{
   foreach(var title in new[]{"iWill.Not.Date.Freddie","iWill.Date.Sam","iWill.Hate.Freddie","iWont.Date.Freddie"})
    Check(TitleMapping.Find("iCarly.S01E08."+title+".WEBRip.srt",guide,"ref")==null);
   Check(TitleMapping.Find(name,[guide[0],new(999,2,1,"iDate Freddie")],"ref")==null);
   Check(TitleMapping.Find("Show.S01E01.iWill.Go.WEBRip.srt",[new(999,1,1,"iWant to Go")],"ref")==null);
   Check(TitleMapping.Find("iCarly.S01E09.WEBRip.srt",guide,"ref")==null);
  });
  test("Title variation accepts full dialogue support but cannot rescue weak or partial dialogue",()=>{
   var cues=Enumerable.Range(0,240).Select(i=>new Cue(i*5,$"topic{i} person{i} question{i} answer{i} action{i} outcome{i}")).ToList();
   var reference=new SubtitleReference("ref",name,"fixture","archive",1,cues);
   var matcher=new SubtitleMatcher(new Source(new(identity,[reference])),System.IO.Path.Combine(temp,"title-variation"));
   var full=matcher.IdentifyAsync(cues,guide,identity,new Progress(),default).GetAwaiter().GetResult();
   Check(full.Result!=null&&SubtitleResult.Valid(SubtitleResult.Row(cues,guide,full),guide,identity));
   Check(matcher.IdentifyAsync(cues.Take(120).ToList(),guide,identity,new Progress(),default).GetAwaiter().GetResult().Result==null);
   Check(matcher.IdentifyAsync([new Cue(0,"Completely unrelated original dialogue here.")],guide,identity,new Progress(),default).GetAwaiter().GetResult().Result==null);
  });
  test("The previous cached title-variation rejection is recalculated",()=>{
   var cues=Enumerable.Range(0,160).Select(i=>new Cue(i*5,$"subject{i} topic{i} action{i} event{i} outcome{i} consequence{i}")).ToList();
   var references=new List<SubtitleReference>{new("ref",name,"fixture","archive",1,cues)};
   var current=identity.Key+":"+SubtitleResult.CatalogHash(guide)+":"+CollectionResolver.Hash(new JsonArray().ToJsonString());
   var referenceHash=CollectionResolver.Hash(JsonSerializer.Serialize(references.OrderBy(r=>r.Id),Data.Options));
   var key=CollectionResolver.Hash(JsonSerializer.Serialize(new{SubtitleResult.Version,comparisonRevision=TranscriptComparison.Revision,multipartRevision=4,current,referenceHash,cues},Data.Options));
   var root=System.IO.Path.Combine(temp,"old-title-variation-cache");
   Data.Save(System.IO.Path.Combine(root,"decisions",key+".json"),new{details=new{reason="Old unmapped title"},result=(object?)null});
   var matcher=new SubtitleMatcher(new Source(new(identity,references)),root);
   Check(matcher.IdentifyAsync(cues,guide,identity,new Progress(),default).GetAwaiter().GetResult().Result!=null);
  });
 }
}
