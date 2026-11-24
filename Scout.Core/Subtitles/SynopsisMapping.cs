namespace Scout.Core.Subtitles;

// A creative episode title need not be spoken. Independently rank guide plots
// against both complete transcripts, then require an agreeing title hint.
internal static class SynopsisMapping {
 public static (ProviderLink? Link,SourceTitleEvidence? Evidence) Find(SubtitleSourceContext? source,IReadOnlyList<Cue> cues,IReadOnlyList<Episode> catalog,SubtitleDecision decision) {
  var best=decision.Best;
  if(source==null||!decision.TextSupported||best==null||best.Candidate.Link!=null||best.Candidate.Parts.Any(p=>p.MappingBlocked||p.Label?.Part>0))return (null,null);
  var name=Path.GetFileName(source.Name);var label=TitleMapping.Label(name);
  if(label?.Part>0||label!=null&&label.First!=label.Last||best.Candidate.Parts.Any(p=>p.Label!=null&&p.Label.First!=p.Label.Last))return (null,null);
  var hint=TitleMapping.Find(name,catalog,best.Candidate.Id);
  // Multipart extent still requires the existing explicit title/range proof.
  if(hint?.Complete!=true||hint.Episodes.Length!=1||hint.Method!="unique-reference-title")return (null,null);
  if(catalog.Count(e=>TitleMapping.SplitTitle(e.Title).Base==TitleMapping.SplitTitle(hint.Episodes[0].Title).Base)!=1)return (null,null);
  if(decision.Ranking.Any(r=>ReferenceMatcher.Strong(r.Overlap)&&r.Candidate.Link!=null&&r.Candidate.Link.Key!=hint.Key))return (null,null);
  var documents=catalog.Select(e=>SourceTitleMapping.Terms(e.Overview??"")).ToArray();
  var frequency=documents.SelectMany(d=>d).GroupBy(w=>w).ToDictionary(g=>g.Key,g=>g.Count());
  var sourceTerms=cues.Select(c=>SourceTitleMapping.Terms(c.Text)).ToArray();
  var referenceTerms=best.Candidate.Index.Words.SelectMany(w=>SourceTitleMapping.Terms(w)).GroupBy(w=>w).ToDictionary(g=>g.Key,g=>g.Count());
  var sourceCounts=sourceTerms.SelectMany(t=>t).GroupBy(w=>w).ToDictionary(g=>g.Key,g=>g.Count());
  int rareLimit=Math.Max(2,(int)Math.Ceiling(catalog.Count*.02));
  var ranked=catalog.Select((episode,i)=>{
   var rare=documents[i].Where(w=>frequency[w]<=rareLimit).ToArray();
   var supported=rare.Where(w=>sourceCounts.GetValueOrDefault(w)>=2&&referenceTerms.GetValueOrDefault(w)>=2).ToArray();
   double Weight(string w)=>Math.Log(1+(double)catalog.Count/frequency[w]);
   double coverage=rare.Length==0?0:supported.Sum(Weight)/rare.Sum(Weight);
   return new{Episode=episode,Supported=supported,Coverage=coverage,Score=coverage*Math.Min(1,supported.Length/4.0)};
  }).OrderByDescending(r=>r.Score).ToArray();
  if(ranked.Length<2)return (null,null);
  var first=ranked[0];double margin=first.Score-ranked[1].Score;
  if(first.Episode.Id!=hint.Episodes[0].Id||first.Supported.Length<4||first.Coverage<.65||margin<.2)return (null,null);
  // The combination must distinguish this plot even when individual concepts
  // recur elsewhere in the show. Avoid accepting a recurring character/object.
  if(documents.Where((_,i)=>catalog[i].Id!=first.Episode.Id).Any(d=>first.Supported.Count(d.Contains)>=first.Supported.Length*.75))return (null,null);
  // Require plot vocabulary beyond the filename and beyond an intro/recap.
  var titleTerms=SourceTitleMapping.Terms(first.Episode.Title);
  if(first.Supported.Count(w=>!titleTerms.Contains(w))<2)return (null,null);
  var sections=new HashSet<int>();int hits=0;
  for(int i=0;i<cues.Count;i++) {
   var window=Enumerable.Range(i,Math.Min(3,cues.Count-i)).TakeWhile(j=>cues[j].Start-cues[i].Start<=15).ToArray();
   var words=window.SelectMany(j=>sourceTerms[j]).ToHashSet();
   if(first.Supported.Count(words.Contains)<2)continue;
   hits++;sections.Add(Math.Min(4,5*i/Math.Max(1,cues.Count)));i+=window.Length-1;
  }
  if(hits<3||sections.Count<3||!sections.Contains(2))return (null,null);
  return (hint with{Method="source-title-with-distinctive-dialogue-plot",EvidenceReferences=best.Candidate.Parts.Select(p=>p.Reference.Id).ToArray()},
   new(name,[],0,first.Supported.Order().ToArray(),first.Coverage,margin));
 }
}
