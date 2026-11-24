using System.Text;
using System.Text.RegularExpressions;
namespace Scout.Core.Subtitles;

// A transparent feasibility measure, not a calibrated episode-assignment policy.
internal sealed record TranscriptIndex(string[] Words,Dictionary<string,List<int>> Phrases);
internal sealed record Overlap(double Score,double SourceCoverage,double ReferenceCoverage,int SharedPhrases,int SourceSections,int ReferenceSections);
internal static class TranscriptComparison {
 public const string Revision="timed-rollup-v2";
 public static TranscriptIndex Combine(TranscriptIndex first,TranscriptIndex second) {
  var phrases=first.Phrases.ToDictionary(p=>p.Key,p=>p.Value.ToList(),StringComparer.Ordinal);
  foreach(var (key,positions) in second.Phrases) {
   if(!phrases.TryGetValue(key,out var list))phrases[key]=list=[];
   list.AddRange(positions.Select(p=>p+first.Words.Length));
  }
  return new(first.Words.Concat(second.Words).ToArray(),phrases);
 }
 public static TranscriptIndex Index(IEnumerable<string> lines) {
  return IndexWords(lines.SelectMany(Words).ToArray());
 }
 static string[] Words(string line) {
   var text=Regex.Replace(line,@"<[^>]+>|\[[^\]]*\]|\([^)]*\)"," ");
   text=Regex.Replace(text,@"^\s*(?:>>\s*)?[A-Z][A-Z .'-]{0,24}:\s*","");
   text=text.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Replace("’","").Replace("'","");
   return Regex.Matches(text,@"[\p{L}\p{N}]+").Select(m=>m.Value).ToArray();
 }
 public static TranscriptIndex Index(IEnumerable<Cue> dialogue) {
  var cues=dialogue.ToArray();var words=cues.Select(c=>Words(c.Text)).ToArray();var overlap=new int[cues.Length];
  for(int i=1;i<cues.Length;i++) {
   double gap=cues[i].Start-cues[i-1].Start;
   if(gap<=0||gap>6||words[i].SequenceEqual(words[i-1]))continue;
   // Require substantial suffix/prefix overlap and multiple consecutive display
   // updates adding new words. Isolated echoes and exact repeated cues stay intact.
   for(int n=Math.Min(words[i-1].Length,words[i].Length-1);n>=3;n--) {
    if(n<Math.Min(words[i-1].Length,words[i].Length)*.4)break;
    if(words[i-1].AsSpan(words[i-1].Length-n).SequenceEqual(words[i].AsSpan(0,n))){overlap[i]=n;break;}
   }
  }
  var output=new List<string>();
  for(int i=0;i<words.Length;i++) {
   bool rolling=overlap[i]>0&&(i>0&&overlap[i-1]>0||i+1<overlap.Length&&overlap[i+1]>0);
   output.AddRange(words[i].Skip(rolling?overlap[i]:0));
  }
  return IndexWords(output.ToArray());
 }
 static TranscriptIndex IndexWords(string[] words) {
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
