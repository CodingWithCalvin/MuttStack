using System.Text.RegularExpressions;
using Scout.Core;
namespace Scout.Core.Subtitles;

sealed record SubtitleLabel(int Season,int First,int Last,int Part,string Family,int TotalParts=0);
sealed record ProviderLink(Episode[] Episodes,string Method,bool Complete,string[] EvidenceReferences) {
 public string Key=>string.Join(",",Episodes.OrderBy(e=>e.Id).Select(e=>e.Id));
}
static class TitleMapping {
 public static string Key(string value)=>Regex.Replace(value.ToLowerInvariant(),@"[^\p{L}\p{N}]","");
 // Release titles sometimes change the first-person opening while retaining
 // the distinctive story wording. Keep that wording exact (including names
 // and negation), require a substantial remainder, and reject catalog ties.
 static string PhraseKey(string key)=>Regex.Replace(key,@"^i(?:will|wantto)(?=.{8,}$)","i");
 static readonly Regex Code=new(@"(?i)(?:\bS(?<s>\d{1,2})[ ._-]*E(?<e>\d{1,3})(?:[ab])?(?:[- ]E?(?<end>\d{1,3})|(?<joined>(?:E\d{1,3})+))?|(?<!\d)(?<s>\d{1,2})x(?<e>\d{1,3})(?:-(?<end>\d{1,3}))?|(?<!\d)(?<s>\d{2})\.(?<e>\d{2})(?:-(?<end>\d{2}))?)(?!\d)");
 static readonly Regex PartSuffix=new(@"(?i)\s*(?:\((?:part\s*)?(?<n>\d+)\)|[-:., ]+part\s*(?<n>\d+)(?:\s+of\s+\d+)?)\s*$");
 public static (string Base,int Part) SplitTitle(string title) {
  var m=PartSuffix.Match(title);return m.Success?(Key(title[..m.Index]),int.Parse(m.Groups["n"].Value)):(Key(title),0);
 }
 public static SubtitleLabel? Label(string name) {
  var file=Path.GetFileNameWithoutExtension(name);var code=Code.Match(file);if(!code.Success)return null;
  var part=Regex.Match(file,@"(?i)\bpart[ ._-]*(\d+)(?:[ ._-]*of[ ._-]*(\d+))?");
  int first=int.Parse(code.Groups["e"].Value),last=code.Groups["end"].Success?int.Parse(code.Groups["end"].Value):first;
  if(code.Groups["joined"].Success) {
   var numbers=Regex.Matches(code.Groups["joined"].Value,@"(?i)E(\d+)").Select(m=>int.Parse(m.Groups[1].Value)).ToArray();
   if(numbers.Where((n,i)=>n!=first+i+1).Any())return null;
   last=numbers[^1];
  }
  if(last<first||last-first>=EpisodeRange.Maximum)return null;
  var family=Key(Code.Replace(Regex.Replace(file,@"(?i)\bpart[ ._-]*\d+(?:[ ._-]*of[ ._-]*\d+)?",""),"CODE"));
  return new(int.Parse(code.Groups["s"].Value),first,last,part.Success?int.Parse(part.Groups[1].Value):0,family,part.Groups[2].Success?int.Parse(part.Groups[2].Value):0);
 }
 public static ProviderLink? Find(string name,IReadOnlyList<Episode> episodes,string referenceId) {
  var file=Path.GetFileNameWithoutExtension(name);var code=Code.Match(file);var label=Label(name);
  if(code.Success&&label==null)return null; // Malformed/nonconsecutive ranges are not whole-story labels.
  var text=code.Success?file[(code.Index+code.Length)..]:file;
  text=Regex.Split(text,@"(?i)\b(?:720p|1080p|2160p|480p|WEBRip|WEB-DL|HDTV|BluRay|DVDRip|x264|x265)\b")[0];
  text=Regex.Replace(text,@"(?i)(?:[._ -]+(?:en|eng|english|sdh|hi))+$","");
  var unnumbered=Regex.Replace(text,@"^\s*\d{1,3}[ ._-]+","");
  var parsedPart=SplitTitle(unnumbered).Part;
  var key=Key(text);var part=label?.Part>0?label.Part:parsedPart;
  var variants=new[]{text,unnumbered}.SelectMany(t=>new[]{Key(t),SplitTitle(t).Base,Key(Regex.Replace(t,@"(?i)[ ._-]*part[ ._-]*\d+(?:[ ._-]*of[ ._-]*\d+)?", ""))}).ToHashSet();
  var groups=episodes.GroupBy(e=>SplitTitle(e.Title).Base).ToArray();
  var exact=groups.Where(g=>g.Key.Length>=2&&variants.Contains(g.Key)).ToArray();
  bool phrasing=false,typo=false;
  if(exact.Length==0) {
   exact=groups.Where(g=>g.Key.Length>=4&&variants.Any(v=>v!=g.Key&&PhraseKey(v)==PhraseKey(g.Key))).ToArray();
   phrasing=exact.Length>0;
  }
  if(exact.Length==0&&label!=null) {
   // A single character typo is allowed only with a long title and an explicit
   // range that independently agrees with the provider's complete multipart group.
   exact=groups.Where(g=>g.Key.Length>=15&&EditDistance(key,g.Key)==1&&g.Count()>1&&RangeAgrees(g.ToArray(),label)).ToArray();typo=exact.Length>0;
  }
  if(exact.Length!=1)return null;
  var items=exact[0].OrderBy(e=>e.Season).ThenBy(e=>e.Number).ToArray();
  if(items.Length==1) {
   // A reference labelled Part 1 of an unsplit provider episode is partial.
   return new(items,typo?"title-near-exact-and-provider-range":phrasing?"unique-reference-title-phrase-variant":"unique-reference-title",part==0,[referenceId]);
  }
  if(items.Length>4||items.Select((e,i)=>(SplitTitle(e.Title).Part==i+1||i==0&&SplitTitle(e.Title).Part==0)&&e.Season==items[0].Season&&e.Number==items[0].Number+i).Any(ok=>!ok))return null;
  if(part>0)return part<=items.Length?new([items[part-1]],"unique-reference-title-and-explicit-part",true,[referenceId]):null;
  if(label!=null&&RangeAgrees(items,label))return new(items,typo?"title-near-exact-and-provider-range":"unique-reference-title-and-complete-range",true,[referenceId]);
  // If the provider calls its first installment by the bare story title,
  // that title alone could describe only the first half. Require an explicit
  // complete range (above), or an explicitly labelled individual part.
  if(SplitTitle(items[0].Title).Part==0)return null;
  // Release catalogs can number a complete story as one episode while the
  // selected provider splits it. Exact story titles establish identity; full
  // dialogue extent is still required before this link can support a match.
  if(!typo&&(label==null||label.First==label.Last))return new(items,"unique-whole-story-title",true,[referenceId]);
  return null;
 }
 static bool RangeAgrees(Episode[] items,SubtitleLabel label)=>label.Part==0&&items.Length==label.Last-label.First+1&&items.All(e=>e.Season==label.Season)&&items.Select(e=>e.Number).Order().SequenceEqual(Enumerable.Range(label.First,items.Length));
 static int EditDistance(string a,string b) {
  var row=Enumerable.Range(0,b.Length+1).ToArray();
  for(int i=1;i<=a.Length;i++){var next=new int[b.Length+1];next[0]=i;for(int j=1;j<=b.Length;j++)next[j]=Math.Min(Math.Min(row[j]+1,next[j-1]+1),row[j-1]+(a[i-1]==b[j-1]?0:1));row=next;}return row[^1];
 }
}
