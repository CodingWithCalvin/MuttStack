using System.Text.Json;
using System.Text.Json.Nodes;
namespace Scout.Core;
public sealed partial class DialogueMatcher {
 // Overflow recovery partitions the guide, never the original dialogue. Every
 // guide entry is searched; batch suggestions are provisional, not assignments.
 private async Task<JsonObject> IdentifyInCatalogBatchesAsync(IReadOnlyList<Cue> cues,IReadOnlyList<Episode> episodes,string model,string endpoint,CancellationToken ct) {
  if(episodes.Count<2)throw new InvalidDataException("The full dialogue and one guide entry exceed the model context. Increase Context tokens before resuming; no dialogue was truncated.");
  var scans=new JsonArray();var candidateIds=new HashSet<int>();
  async Task Scan(IReadOnlyList<Episode> batch) {
   ct.ThrowIfCancellationRequested();JsonObject scan;
   try {scan=await DirectAsync(cues,batch,model,endpoint,false,ct,"This is one partition of a larger episode guide. Use only evidence in the full original dialogue. Any suggestion remains provisional until compared with candidates from the other partitions.");}
   catch(ModelContextException) when(batch.Count>1) {await Split(batch);return;}
   catch(InvalidDataException ex) when(GroundableCombination(ex)) {
    // Preserve proposed identities for the final verifier; never repair adjacency.
    var attempts=JsonNode.Parse((string)ex.Data["model_attempts"]!)!.AsArray();scan=attempts.Last()!["response"]!.DeepClone().AsObject();
   }
   scans.Add(new JsonObject{["catalog_ids"]=JsonSerializer.SerializeToNode(batch.Select(e=>e.Id)),["result"]=scan.DeepClone()});
   foreach(var node in scan.Array("matches").Concat(scan.Array("candidate_checks")).Concat(scan.Array("alternatives"))) {
    var found=batch.FirstOrDefault(e=>e.Id==node.Int("episode_id",-1)||e.Title==node.Text("episode_title"));if(found!=null)candidateIds.Add(found.Id);
   }
  }
  async Task Split(IReadOnlyList<Episode> batch) {
   int mid=batch.Count/2;await Scan(batch.Take(mid).ToArray());await Scan(batch.Skip(mid).ToArray());
  }
  try {
   await Split(episodes);
   foreach(var episode in Rank(cues,episodes))candidateIds.Add(episode.Id);
   var candidates=episodes.Where(e=>candidateIds.Contains(e.Id)).ToArray();
   if(candidates.Length==0||candidates.Length>=episodes.Count)throw new InvalidDataException("The guide partitions did not produce a smaller candidate set. Increase Context tokens before resuming; no candidates or dialogue were silently discarded.");
   const string instruction="Independently verify these candidates against the entire original dialogue. They were retrieved from all partitions of a larger guide. No earlier answers are supplied. Abstain unless the defining guide events agree.";
   async Task<JsonObject> Verify(string task) {
    JsonObject result;
    try{result=await DirectAsync(cues,candidates,model,endpoint,true,ct,task);}
    catch(InvalidDataException ex) when(GroundableCombination(ex)){return await AuditRejectedCombinationAsync(ex,cues,candidates,model,endpoint,ct);}
    return result.Array("matches").Count>1?await VerifySavedProposalAsync(cues,candidates,result,model,endpoint,ct):result;
   }
   var first=await Verify(instruction);var second=await Verify(instruction+" Audit the original source independently again.");
   string Decision(JsonObject r)=>r.Text("assessment")+":"+r.Text("coverage")+":"+string.Join(",",r.Array("matches").Select(m=>m.Int("episode_id")));
   if(Decision(first)!=Decision(second))throw new InvalidDataException("Independent reviews of the guide-partition candidates disagree. No assignment accepted.");
   second["context_recovery"]=new JsonObject{["method"]="full-dialogue-catalog-partitions-v1",["catalog_count"]=episodes.Count,["batches"]=scans.DeepClone(),["candidate_ids"]=JsonSerializer.SerializeToNode(candidates.Select(e=>e.Id)),["independent_result"]=first};
   second["request_adjustments"]=$"Context overflow: searched all {episodes.Count} guide entries in {scans.Count} partitions, then independently verified the retrieved candidates twice. Every request retained the complete original dialogue. No automatic context or runtime change.";
   return second;
  }catch(Exception ex) {
   ex.Data["context_recovery"]=new JsonObject{["catalog_count"]=episodes.Count,["batches"]=scans.DeepClone()}.ToJsonString();throw;
  }
 }
}
