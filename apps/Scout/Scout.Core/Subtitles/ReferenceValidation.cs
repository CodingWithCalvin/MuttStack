using System.Text.RegularExpressions;

namespace Scout.Core.Subtitles;

public sealed record RejectedReference(string Id,string Name,string Reason);
public sealed record ValidatedReferences(List<SubtitleReference> References,List<RejectedReference> Rejected) {
 public string Note=>Rejected.Count==0?"":$"{Rejected.Count} suspect subtitle reference(s) quarantined: "+string.Join("; ",Rejected.Take(3).Select(r=>r.Name+" — "+r.Reason))+". These files cannot support an episode match.";
}

// A provider's series ID and a plausible filename do not validate the contents
// of an uploaded subtitle. Use conservative checks; lack of a title alone is
// not corruption and must remain available for dialogue-based identity bridges.
public static class ReferenceValidation {
 public const int Revision=1;
 public static ValidatedReferences Check(IReadOnlyList<SubtitleReference> references,IReadOnlyList<Episode> catalog) {
  var rejected=new List<RejectedReference>();var valid=new List<SubtitleReference>();
  foreach(var r in references) {
   string? reason=null;
   if(r.Cues==null||r.Cues.Count==0||r.Cues.Any(c=>!double.IsFinite(c.Start)||c.Start<0||string.IsNullOrWhiteSpace(c.Text))||r.Cues.Zip(r.Cues.Skip(1)).Any(p=>p.First.Start>p.Second.Start))reason="invalid or unordered dialogue cues";
   else {
    var letters=string.Concat(r.Cues.Select(c=>c.Text)).Where(char.IsLetter).ToArray();
    if(letters.Length>=200&&letters.Count(c=>c is >= '\u0041' and <= '\u024f')<letters.Length*.5)reason="dialogue is not an English reference";
   }
   if(reason!=null)rejected.Add(new(r.Id,r.Name,reason));else valid.Add(r);
  }
  var vocabulary=valid.Select(r=>Regex.Matches(string.Join(" ",r.Cues.Select(c=>c.Text)).ToLowerInvariant(),@"\p{L}+").Select(m=>m.Value).ToHashSet()).ToArray();
  // Learn recurring names from the selected catalog, then corroborate them in
  // independent reference transcripts. No show names or character names are built in.
  var cohort=valid.Select((r,i)=>(Index:i,Hash:SubtitleResult.DialogueHash(r.Cues))).DistinctBy(x=>x.Hash).Select(x=>x.Index).ToArray();
  var anchors=catalog.SelectMany(e=>Regex.Matches(e.Overview??"",@"(?<=[a-z,] )[A-Z][a-z]{2,}\b").Select(m=>m.Value.ToLowerInvariant()).Distinct())
   .GroupBy(w=>w).Where(g=>g.Count()>=3).Select(g=>g.Key)
   .Where(w=>cohort.Count(i=>vocabulary[i].Contains(w))>=Math.Max(3,cohort.Length*.2)).ToArray();
  if(cohort.Length>=8&&anchors.Length>=4&&cohort.Count(i=>anchors.Count(vocabulary[i].Contains)>=2)>=cohort.Length*.9) {
   for(int i=valid.Count-1;i>=0;i--)if(valid[i].Cues.Count>=150&&!anchors.Any(vocabulary[i].Contains)) {
    rejected.Add(new(valid[i].Id,valid[i].Name,"content lacks the recurring series names supported by the catalog and other references; it may belong to another show"));valid.RemoveAt(i);
   }
  }
  return new(valid,rejected);
 }
}
