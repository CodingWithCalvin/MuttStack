using System.Text.RegularExpressions;

namespace Scout.Core.Subtitles;

public sealed record SubtitleSourceContext(string Name);
internal sealed record SourceTitleEvidence(string Name,string[] TitleTerms,int TitleMentions,string[] SynopsisTerms,double SynopsisCoverage,double SynopsisMargin);
internal static class SourceTitleMapping {
 // A source filename is a scoped hint, never a reusable reference identity.
 // It needs both a complete reference match and distinctive synopsis support.
 public static (ProviderLink? Link,SourceTitleEvidence? Evidence) Find(SubtitleSourceContext? source,IReadOnlyList<Cue> cues,
  IReadOnlyList<Episode> catalog,SubtitleDecision decision) {
  var title=FindTitle(source,cues,catalog,decision);
  return title.Link!=null?title:SynopsisMapping.Find(source,cues,catalog,decision);
 }
 static (ProviderLink? Link,SourceTitleEvidence? Evidence) FindTitle(SubtitleSourceContext? source,IReadOnlyList<Cue> cues,IReadOnlyList<Episode> catalog,SubtitleDecision decision) {
  var best=decision.Best;
  if(source==null||!decision.TextSupported||best==null||best.Candidate.Link!=null||best.Candidate.Parts.Any(p=>p.MappingBlocked))return (null,null);
  var name=Path.GetFileName(source.Name);
  if(TitleMapping.Label(name)?.Part>0||best.Candidate.Parts.Any(p=>p.Label?.Part>0))return (null,null);
  var hint=TitleMapping.Find(name,catalog,best.Candidate.Id);
  if(hint?.Complete!=true||hint.Method.Contains("phrase")||hint.Method.Contains("near-exact"))return (null,null);
  // Every nearby reference with a known title must agree. Conflicting source
  // filenames must not override a titled or user-reviewed reference identity.
  if(decision.Ranking.Any(r=>ReferenceMatcher.Strong(r.Overlap)&&r.Candidate.Link!=null&&r.Candidate.Link.Key!=hint.Key))return (null,null);
  if(hint.Episodes.Length>1) {
   var label=TitleMapping.Label(name);
   if(label==null||label.Part!=0||label.Season!=hint.Episodes[0].Season||label.First!=hint.Episodes[0].Number||label.Last!=hint.Episodes[^1].Number)return (null,null);
  }
  var groups=catalog.GroupBy(e=>TitleMapping.SplitTitle(e.Title).Base).Select(g=>g.ToArray()).ToArray();
  if(hint.Episodes.Length==1&&groups.Any(g=>g.Length>1&&g.Any(e=>e.Id==hint.Episodes[0].Id)))return (null,null);
  // A synopsis bag of words alone can describe several stories. Require
  // distinctive title terms together in repeated, separated dialogue windows.
  // Creative titles absent from dialogue stay held for titled references/review.
  var titleDocuments=groups.Select(g=>Terms(string.Join(" ",g.Select(e=>Regex.Replace(e.Title,@"^i(?=[A-Z])",""))))).ToArray();
  var titleFrequency=titleDocuments.SelectMany(d=>d).GroupBy(w=>w).ToDictionary(g=>g.Key,g=>g.Count());
  var titleTerms=Terms(string.Join(" ",hint.Episodes.Select(e=>Regex.Replace(e.Title,@"^i(?=[A-Z])",""))))
   .Where(w=>titleFrequency.GetValueOrDefault(w)<=Math.Max(2,(int)Math.Ceiling(groups.Length*.03))).Order().ToArray();
  if(titleTerms.Length==0||titleTerms.Length==1&&(titleTerms[0].Length<5||titleFrequency[titleTerms[0]]!=1))return (null,null);
  var reference=Terms(string.Join(" ",best.Candidate.Index.Words));
  var recording=Terms(string.Join(" ",cues.Select(c=>c.Text)));
  if(!titleTerms.All(reference.Contains)||!titleTerms.All(recording.Contains))return (null,null);
  var titleHits=new List<int>();
  for(int i=0;i<cues.Count;i++) {
   var window=cues.Skip(i).Take(3).TakeWhile(c=>c.Start-cues[i].Start<=15).ToArray();
   if(!titleTerms.All(Terms(string.Join(" ",window.Select(c=>c.Text))).Contains))continue;
   titleHits.Add(i);i+=window.Length-1;
  }
  var titleSections=titleHits.Select(i=>Math.Min(4,5*i/Math.Max(1,cues.Count))).Distinct().ToArray();
  if(titleHits.Count<3||titleSections.Length<2||!titleSections.Any(s=>s is >=1 and <=3))return (null,null);
  var documents=groups.Select(g=>Terms(string.Join(" ",g.Select(e=>e.Overview)))).ToArray();
  var frequency=documents.SelectMany(d=>d).GroupBy(w=>w).ToDictionary(g=>g.Key,g=>g.Count());
  var shared=reference.Intersect(recording).ToHashSet();
  var rareLimit=Math.Max(2,(int)Math.Ceiling(documents.Length*.05));
  var ranked=groups.Select((g,i)=>{
   var words=documents[i];var rare=words.Where(w=>frequency[w]<=rareLimit).ToArray();
   var supported=rare.Where(shared.Contains).ToArray();
   double Weight(string w)=>Math.Log(1+(double)documents.Length/frequency[w]);
   double coverage=rare.Length==0?0:supported.Sum(Weight)/rare.Sum(Weight);
   return new{Episodes=g,Supported=supported,Coverage=coverage,Eligible=supported.Length>=2&&words.Count(shared.Contains)>=3&&coverage>=.55};
  }).OrderByDescending(x=>x.Coverage).ToArray();
  var match=ranked.FirstOrDefault();
  if(match==null||!match.Eligible||!hint.Episodes.All(e=>match.Episodes.Any(m=>m.Id==e.Id)))return (null,null);
  double margin=match.Coverage-(ranked.Skip(1).FirstOrDefault()?.Coverage??0);
  if(margin<.15)return (null,null);
  // Require story-specific support beyond an opening announcement or recap.
  var hits=cues.Where(c=>Terms(c.Text).Overlaps(match.Supported)).ToArray();
  if(hits.Length<3||cues.Count<3)return (null,null);
  var sections=cues.Select((c,i)=>(Cue:c,Index:i)).Where(p=>hits.Contains(p.Cue)).Select(p=>Math.Min(4,5*p.Index/cues.Count)).Distinct().Count();
  if(sections<2)return (null,null);
  return (hint with{Method="source-title-with-reference-and-synopsis",EvidenceReferences=best.Candidate.Parts.Select(p=>p.Reference.Id).ToArray()},
   new(name,titleTerms,titleHits.Count,match.Supported.Order().ToArray(),match.Coverage,margin));
 }
 static readonly HashSet<string> Stop=LoadStop();
 static HashSet<string> LoadStop() {
  using var stream=typeof(SourceTitleMapping).Assembly.GetManifestResourceStream("Scout.Core.Assets.stopwords.txt")!;
  using var reader=new StreamReader(stream);return reader.ReadToEnd().Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).ToHashSet();
 }
 static string Stem(string word) {
  if(word.Length>5&&word.EndsWith("ies"))return word[..^3]+"y";
  if(word.Length>5&&word.EndsWith("ing"))return word[..^3];
  if(word.Length>5&&word.EndsWith("ed")&&!word.EndsWith("eed"))return word[..^2];
  if(word.Length>3&&word.EndsWith('s')&&!word.EndsWith("ss"))return word[..^1];
  return word;
 }
 internal static HashSet<string> Terms(string text)=>Regex.Matches(text.ToLowerInvariant(),@"[a-z]{3,}").Select(m=>m.Value)
  .Where(w=>!Stop.Contains(w)).Select(Stem).Where(w=>w.Length>=3&&!Stop.Contains(w)).ToHashSet();
}
