using Scout.Core;
namespace Scout.Core.Subtitles;

sealed record ReferenceCandidate(string Id,MappedReference[] Parts,TranscriptIndex Index,ProviderLink? Link) {
 public string Name=>string.Join(" + ",Parts.Select(r=>r.Reference.Name));
}
sealed record PartSupport(string ReferenceId,double SourceCoverage,double ReferenceCoverage,int SharedPhrases,int ReferenceSections,double StartFraction,double EndFraction);
sealed record CandidateScore(ReferenceCandidate Candidate,Overlap Overlap,bool OrderedParts,PartSupport[] Parts,bool CompleteExtent);
sealed record SubtitleDecision(bool TextSupported,bool ProviderMapped,string Reason,CandidateScore? Best,double Margin,CandidateScore[] Ranking);
static class ReferenceMatcher {
 public static bool Strong(Overlap x)=>x.SharedPhrases>=60&&x.SourceCoverage>=.45&&x.ReferenceCoverage>=.45&&x.SourceSections>=3&&x.ReferenceSections>=3;
 public static List<ReferenceCandidate> Candidates(IReadOnlyList<MappedReference> references,HashSet<string> common,CancellationToken ct=default) {
  var anchors=references.Where(r=>r.Link!=null).ToArray();
  var inverted=new Dictionary<string,List<int>>(StringComparer.Ordinal);
  for(int i=0;i<anchors.Length;i++)foreach(var phrase in anchors[i].Reference.Index.Phrases.Keys.Where(p=>!common.Contains(p))) {
   if(!inverted.TryGetValue(phrase,out var ids))inverted[phrase]=ids=[];ids.Add(i);
  }
  ProviderLink? Bridge(TranscriptIndex index,ProviderLink? direct,bool partial) {
   ct.ThrowIfCancellationRequested();
   var hits=new Dictionary<int,int>();
   foreach(var phrase in index.Phrases.Keys)if(inverted.TryGetValue(phrase,out var ids))foreach(int id in ids)hits[id]=hits.GetValueOrDefault(id)+1;
   // The 60-shared-sequence rule is exact: this index skips only references
   // that cannot pass the existing acceptance rule, without changing scores.
   var ranked=hits.Where(p=>p.Value>=60).Select(p=>anchors[p.Key]).Select(a=>new{a,score=TranscriptComparison.Compare(index,a.Reference.Index,common)})
    .Where(x=>Strong(x.score)).GroupBy(x=>x.a.Link!.Key)
    .Select(g=>g.OrderByDescending(x=>x.score.Score).First()).OrderByDescending(x=>x.score.Score).ToArray();
   if(ranked.Length==0)return direct;
   var best=ranked[0];
   if(ranked.Length>1&&best.score.Score-ranked[1].score.Score<.15)return null;
   if(direct!=null&&direct.Key!=best.a.Link!.Key)return null;
   if(direct!=null)return direct;
   var link=best.a.Link!;
   return link with {Method="dialogue-link-to-provider-titled-reference",Complete=link.Complete&&!partial,EvidenceReferences=[best.a.Reference.Id]};
  }
  var mapped=references.Select(r=>r with {Link=Bridge(r.Reference.Index,r.Link,r.Label?.Part>0)}).ToArray();
  var result=mapped.Select(r=>new ReferenceCandidate(r.Reference.Id,[r],r.Reference.Index,r.Link)).ToList();
  foreach(var first in mapped.Where(r=>r.Label is {Part:1,TotalParts:0 or 2})) {
   if(mapped.Any(r=>r.Archive==first.Archive&&r.Label?.Season==first.Label!.Season&&r.Label.Family==first.Label.Family&&r.Label.Part>2))continue;
   var second=mapped.Where(r=>r.Label is {Part:2,TotalParts:0 or 2}&&r.Archive==first.Archive&&r.Reference.Series==first.Reference.Series&&r.Label.Season==first.Label!.Season&&r.Label.Family==first.Label.Family&&r.Label.First>=first.Label.First&&r.Label.First<=first.Label.Last+1).ToArray();
   if(second.Length!=1)continue;
   // Exactly one counterpart in the release family; ambiguous editions stay single.
   var next=second[0];
   if(mapped.Count(r=>r.Label?.Part==1&&r.Archive==first.Archive&&r.Label.Family==first.Label!.Family&&r.Label.Season==first.Label.Season&&next.Label!.First>=r.Label.First&&next.Label.First<=r.Label.Last+1)!=1)continue;
   var combined=TranscriptComparison.Combine(first.Reference.Index,next.Reference.Index);
   ProviderLink? link=null;
   if(first.Link is {} a&&next.Link is {} b) {
    var episodes=a.Episodes.Concat(b.Episodes).DistinctBy(e=>e.Id).OrderBy(e=>e.Season).ThenBy(e=>e.Number).ToArray();
    if(a.Key==b.Key&&episodes.Length==1&&!a.Complete&&!b.Complete||a.Key!=b.Key&&a.Complete&&b.Complete&&episodes.Length==2&&episodes[0].Season==episodes[1].Season&&episodes[0].Number+1==episodes[1].Number)
     link=new(episodes,"ordered-reference-parts",true,a.EvidenceReferences.Concat(b.EvidenceReferences).Distinct().ToArray());
   }
   link=Bridge(combined,link,false);
   result.Add(new(first.Reference.Id+"+"+next.Reference.Id,[first,next],combined,link));
  }
  return result;
 }
 public static SubtitleDecision Match(TranscriptIndex source,IReadOnlyList<ReferenceCandidate> candidates,HashSet<string> common,CancellationToken ct=default) {
  ct.ThrowIfCancellationRequested();
  var ranking=candidates.Select(c=>{ct.ThrowIfCancellationRequested();return Score(source,c,common);}).OrderByDescending(s=>s.Overlap.Score).ThenBy(s=>s.Candidate.Id,StringComparer.Ordinal).ToArray();
  var best=ranking.FirstOrDefault();if(best==null)return new(false,false,"No reference dialogue available.",null,0,[]);
  bool Equivalent(CandidateScore other) {
   if(other.Candidate.Id==best.Candidate.Id)return true;
   if(best.Candidate.Link!=null&&other.Candidate.Link?.Key==best.Candidate.Link.Key)return true;
   if(best.OrderedParts&&best.Candidate.Parts.Length==2&&other.Candidate.Parts.All(p=>best.Candidate.Parts.Any(b=>b.Reference.Id==p.Reference.Id)))return true;
   return best.Candidate.Index.Words.SequenceEqual(other.Candidate.Index.Words);
  }
  double margin=best.Overlap.Score-(ranking.FirstOrDefault(r=>!Equivalent(r))?.Overlap.Score??0);
  bool text=Strong(best.Overlap)&&best.OrderedParts&&best.CompleteExtent&&margin>=.15;
  bool mapped=text&&best.Candidate.Link?.Complete==true;
  string reason=!Strong(best.Overlap)?"No reference covers enough distinctive dialogue.":!best.OrderedParts?"Both parts must cover substantial, separate, ordered portions; recaps/promos do not qualify.":!best.CompleteExtent?"Shared dialogue does not extend through both the recording and reference; possible partial recording.":margin<.15?"Competing dialogue references remain too close.":best.Candidate.Link==null?"Dialogue matches, but a unique provider title/ID mapping is missing or conflicting.":!best.Candidate.Link.Complete?"Only part of a provider episode is represented.":"Dialogue correspondence and a unique provider catalog mapping are supported.";
  return new(text,mapped,reason,best,margin,ranking.Take(8).ToArray());
 }
 static CandidateScore Score(TranscriptIndex source,ReferenceCandidate candidate,HashSet<string> common) {
  var overall=TranscriptComparison.Compare(source,candidate.Index,common);
  var sharedOverall=source.Phrases.Keys.Intersect(candidate.Index.Phrases.Keys).Where(p=>!common.Contains(p)).ToArray();
  bool Extent(TranscriptIndex index) {
   var positions=sharedOverall.SelectMany(p=>index.Phrases[p]).Order().ToArray();if(positions.Length==0)return false;
   return positions[(int)(positions.Length*.1)]/(double)Math.Max(1,index.Words.Length)<=.25&&positions[Math.Min(positions.Length-1,(int)(positions.Length*.9))]/(double)Math.Max(1,index.Words.Length)>=.75;
  }
  bool complete=Extent(source)&&Extent(candidate.Index);
  if(candidate.Parts.Length==1)return new(candidate,overall,true,[],complete);
  var support=new List<PartSupport>();
  foreach(var part in candidate.Parts) {
   var index=part.Reference.Index;
   var excluded=common.Concat(candidate.Parts.Where(p=>p!=part).SelectMany(p=>p.Reference.Index.Phrases.Keys)).ToHashSet();
   var shared=source.Phrases.Keys.Intersect(index.Phrases.Keys).Where(p=>!excluded.Contains(p)).ToArray();
   var positions=shared.SelectMany(p=>source.Phrases[p]).Order().ToArray();
   double start=positions.Length==0?0:positions[(int)(positions.Length*.1)]/(double)Math.Max(1,source.Words.Length);
   double end=positions.Length==0?0:positions[Math.Min(positions.Length-1,(int)(positions.Length*.9))]/(double)Math.Max(1,source.Words.Length);
   var overlap=TranscriptComparison.Compare(source,index,excluded);
   support.Add(new(part.Reference.Id,shared.Length/(double)Math.Max(1,source.Phrases.Keys.Count(p=>!common.Contains(p))),overlap.ReferenceCoverage,shared.Length,overlap.ReferenceSections,start,end));
  }
  bool ordered=support.All(s=>s.SharedPhrases>=60&&s.SourceCoverage>=.15&&s.ReferenceCoverage>=.45&&s.ReferenceSections>=3&&s.EndFraction-s.StartFraction>=.15)&&support[0].EndFraction<support[1].StartFraction;
  return new(candidate,overall,ordered,support.ToArray(),complete);
 }
}
