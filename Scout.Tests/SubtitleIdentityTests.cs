using System.Text.Json.Nodes;
using Scout.Core;
using Scout.Core.Subtitles;

static class SubtitleIdentityTests {
 sealed class Source(SubtitleReferenceSet set):ISubtitleReferenceSource {public Task<SubtitleReferenceSet> LoadAsync(SeriesIdentity s,IReadOnlyList<Episode> c,IProgress<string> p,CancellationToken ct)=>Task.FromResult(set);}
 sealed class Progress:IProgress<string>{public void Report(string value){}}
 static void Check(bool condition,string message="Subtitle identity regression"){if(!condition)throw new Exception(message);}
 public static void Run(Action<string,Action> test,string temp) {
  var series=new SeriesIdentity("tmdb","123","Example");
  Episode[] guide=[new(101,1,7,"Moonlight Rescue","The captain rescues a stranded astronaut with a silver ladder beside the lunar crater."),new(102,1,8,"Garden Surprise","The gardener plants purple tulips beside a wooden greenhouse.")];
  var cues=Enumerable.Range(0,180).Select(i=>new Cue(i*5,$"Moonlight rescue captain rescues stranded astronaut silver ladder lunar crater subject{i} action{i} result{i} finale{i}")).ToList();
  var numbered=new SubtitleReference("r","Example.S03E05.WEBRip.srt","fixture","archive",3,cues);
  SubtitleAttempt Match(string key,string? name,List<Cue>? text=null,List<SubtitleReference>? refs=null,Episode[]? catalog=null) =>new SubtitleMatcher(new Source(new(series,refs??[numbered])),Path.Combine(temp,"subtitle-identity",key))
   .IdentifyAsync(text??cues,catalog??guide,series,new Progress(),default,name==null?null:new(name)).GetAwaiter().GetResult();
  const string filename="Example.S02E03.Moonlight.Rescue.WEBRip.mkv";
  test("A unique filename title needs complete subtitle and distinctive synopsis support",()=>{
   var result=Match("supported",filename);Check(result.Result!=null&&result.Details.Array("selected_episodes").Single()!.Int("id")==101);
   Check(result.Details["source_title_evidence"]!.Array("synopsis_terms").Count>=2&&SubtitleResult.Valid(SubtitleResult.Row(cues,guide,result),guide,series));
   Check(result.Details["best"]!["link"].Text("method")=="source-title-with-reference-and-synopsis");
   var row=SubtitleResult.Row(cues,guide,result);row["path"]=Path.Combine(temp,"source-title.mkv");row["duration"]=900;
   var report=new JsonObject{["files"]=new JsonArray(row)};
   Check(EvidenceReview.Create(CollectionResolver.Resolve(report).Single()).Sections.Any(s=>s.Heading=="Corroborated source title"));
  });
  test("Filename hints cannot accept number-only names, wrong titles, weak dialogue or partial recordings",()=>{
   foreach(var name in new string?[]{null,"Example.S01E07.mkv","Example.S01E08.Garden.Surprise.mkv","Example.S01E07.Moonlight.Rescues.mkv"})Check(Match("held-"+CollectionResolver.Hash(name??"none"),name).Result==null);
   Check(Match("partial",filename,cues.Take(70).ToList()).Result==null);
   Check(Match("weak",filename,[new(0,"unrelated dialogue with no distinctive sequences")]).Result==null);
   Check(Match("no-synopsis",filename,catalog:guide.Select(e=>e with{Overview=""}).ToArray()).Result==null);
   var noTitle=cues.Select(c=>c with{Text=c.Text.Replace("Moonlight rescue ","")}).ToList();
   Check(Match("no-title-dialogue",filename,noTitle,[numbered with{Cues=noTitle}]).Result==null);
   var openingOnly=noTitle.Select((c,i)=>i<3?c with{Text="Moonlight rescue "+c.Text}:c).ToList();
   Check(Match("opening-only-title",filename,openingOnly,[numbered with{Cues=openingOnly}]).Result==null);
  });
  test("Competing synopses and title duplicates prevent a filename from manufacturing identity",()=>{
   Check(Match("ambiguous-title",filename,catalog:[..guide,new(103,2,1,"Moonlight Rescue",guide[0].Overview)]).Result==null);
   Check(Match("ambiguous-story",filename,catalog:[..guide,new(103,2,1,"Another Rescue",guide[0].Overview)]).Result==null);
  });
  test("Filename-conditioned matches cannot leak through a shared comparison cache",()=>{
   Check(Match("shared-cache",filename).Result!=null);
   Check(Match("shared-cache","Example.S01E08.Garden.Surprise.mkv").Result==null);
   Check(Match("shared-cache",null).Result==null);
  });
  test("Near-identical differently numbered subtitle versions do not compete with each other",()=>{
   var alternate=numbered with{Id="r2",Name="Example.S03E20.WEBRip.srt",Cues=cues.Select((c,i)=>i==75?c with{Text=c.Text+" correction"}:c).ToList()};
   var result=Match("duplicates",filename,refs:[numbered,alternate]);Check(result.Result!=null&&result.Details.Array("equivalent_references").Count==1);
   Check(Match("duplicates-without-identity",null,refs:[numbered,alternate]).Result==null);
   Check(!SubdlReferences.CoveredEpisodes([numbered,alternate],[new(5,3,5,"Five"),new(20,3,20,"Twenty")]).Any());
  });
  test("Three duplicate uploads cannot turn their story dialogue into discarded boilerplate",()=>{
   var duplicates=Enumerable.Range(0,3).Select(i=>numbered with{Id="duplicate-"+i,Name=$"Example.S03E{i+1:00}.srt",Cues=cues.Select((c,n)=>n==75+i?c with{Text=c.Text+" correction"}:c).ToList()}).ToList();
   var result=Match("triple-duplicates",filename,refs:duplicates);
   Check(result.Result!=null&&result.Details.Array("equivalent_references").Count==2);
   Check(SubdlReferences.CoveredEpisodes(duplicates,Enumerable.Range(1,3).Select(i=>new Episode(i,3,i,"Unlisted "+i)).ToArray()).Count==0);
  });
  test("Conflicting titles on duplicate content remain blocked even with an agreeing filename",()=>{
   var left=numbered with{Name="Example.S01E07.Moonlight.Rescue.srt"};
   var right=numbered with{Id="r2",Name="Example.S01E08.Garden.Surprise.srt"};
   Check(Match("conflicting-titles",filename,refs:[left,right]).Result==null);
   Check(SubdlReferences.CoveredEpisodes([left,right],guide).Count==0);
  });
  test("Partial and similar recordings cannot be collapsed as duplicate references",()=>{
   var whole=TranscriptComparison.Index(cues);var partial=TranscriptComparison.Index(cues.Take(90));
   var different=TranscriptComparison.Index(cues.Select((c,i)=>i<120?c:new Cue(c.Start,$"different{i} another{i} unique{i} plot{i} ending{i}")));
   Check(!ReferenceMatcher.SameContent(whole,partial,[])&&!ReferenceMatcher.SameContent(whole,different,[]));
  });
  test("Per-file release metadata maps numbered references without borrowing a pack identity",()=>{
   var metadata=numbered with{TitleNames=["Example.S03E05.Moonlight.Rescue.srt"]};
   Check(Match("metadata",null,refs:[metadata]).Result!=null);
   Check(Match("metadata-conflict",filename,refs:[metadata with{TitleNames=[..metadata.TitleNames!,"Example.S01E08.Garden.Surprise.srt"]}]).Result==null);
  });
  test("An unnumbered first installment groups with explicitly numbered later parts",()=>{
   Episode[] parts=[guide[0] with{Number=1,Title="Moonlight Rescue"},new(103,1,2,"Moonlight Rescue - Part 2",guide[0].Overview)];
   Check(TitleMapping.Find("Example.S01E01E02.Moonlight.Rescue.srt",parts,"ref")?.Episodes.Length==2);
   Check(TitleMapping.Find("Example.S01E01E03.Moonlight.Rescue.srt",parts,"ref")==null);
   Check(TitleMapping.Find("Example.S01E01.Moonlight.Rescue.srt",parts,"ref")==null);
   Check(TitleMapping.Find("Example.S01E01.Moonlight.Rescue.Part.1.srt",parts,"ref")?.Episodes.Length==1);
   Check(Match("part-hint","Example.S01E01.Moonlight.Rescue.Part.1.mkv",catalog:parts).Result==null);
  });
 }
}
