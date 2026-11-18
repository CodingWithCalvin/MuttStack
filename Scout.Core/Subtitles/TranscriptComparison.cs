using System.Text;
using System.Text.RegularExpressions;
namespace Scout.Core.Subtitles;

// A transparent feasibility measure, not a calibrated episode-assignment policy.
internal sealed record TranscriptIndex(string[] Words,Dictionary<string,List<int>> Phrases);
internal sealed record Overlap(double Score,double SourceCoverage,double ReferenceCoverage,int SharedPhrases,int SourceSections,int ReferenceSections);
internal static class TranscriptComparison {
 public static TranscriptIndex Combine(TranscriptIndex first,TranscriptIndex second) {
  var phrases=first.Phrases.ToDictionary(p=>p.Key,p=>p.Value.ToList(),StringComparer.Ordinal);
  foreach(var (key,positions) in second.Phrases) {
   if(!phrases.TryGetValue(key,out var list))phrases[key]=list=[];
   list.AddRange(positions.Select(p=>p+first.Words.Length));
  }
  return new(first.Words.Concat(second.Words).ToArray(),phrases);
 }
 public static TranscriptIndex Index(IEnumerable<string> lines) {
  var words=lines.SelectMany(line=> {
   var text=Regex.Replace(line,@"<[^>]+>|\[[^\]]*\]|\([^)]*\)"," ");
   text=Regex.Replace(text,@"^\s*(?:>>\s*)?[A-Z][A-Z .'-]{0,24}:\s*","");
   text=text.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Replace("’","").Replace("'","");
   return Regex.Matches(text,@"[\p{L}\p{N}]+").Select(m=>m.Value);
  }).ToArray();
  var phrases=new Dictionary<string,List<int>>(StringComparer.Ordinal);
  for(int i=0;i+5<=words.Length;i++) {
   var key=string.Join(" ",words.Skip(i).Take(5));
   if(!phrases.TryGetValue(key,out var positions))phrases[key]=positions=[];
   positions.Add(i);
  }
  return new(words,phrases);
 }
 public static HashSet<string> Common(IReadOnlyList<TranscriptIndex> references) {
  if(references.Count<3)return [];
  int limit=Math.Max(3,(int)Math.Ceiling(references.Count*.4));
  return references.SelectMany(r=>r.Phrases.Keys).GroupBy(p=>p).Where(g=>g.Count()>=limit).Select(g=>g.Key).ToHashSet();
 }
 public static Overlap Compare(TranscriptIndex source,TranscriptIndex reference,HashSet<string> common) {
  var left=source.Phrases.Keys.Where(p=>!common.Contains(p)).ToHashSet();
  var right=reference.Phrases.Keys.Where(p=>!common.Contains(p)).ToHashSet();
  var shared=left.Intersect(right).ToArray();
  double a=shared.Length/(double)Math.Max(1,left.Count),b=shared.Length/(double)Math.Max(1,right.Count);
  int Sections(TranscriptIndex index)=>shared.SelectMany(p=>index.Phrases[p]).Select(i=>Math.Min(4,5*i/Math.Max(1,index.Words.Length))).Distinct().Count();
  return new(a+b==0?0:2*a*b/(a+b),a,b,shared.Length,Sections(source),Sections(reference));
 }
}
