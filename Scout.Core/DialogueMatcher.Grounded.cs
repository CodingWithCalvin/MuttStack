using System.Text.Json;
using System.Text.Json.Nodes;
namespace Scout.Core;
public sealed partial class DialogueMatcher
{
    /// <summary>Reuses proposed IDs only. No prior rationale, titles or numbering are sent to the focused verifier.</summary>
    public async Task<JsonObject> VerifySavedProposalAsync(IReadOnlyList<Cue> cues,IReadOnlyList<Episode> catalog,JsonObject proposal,string model,string endpoint,CancellationToken ct=default)
    {
        ValidateEndpoint(endpoint);if(cues.Count==0)throw new InvalidDataException("No original dialogue to audit.");
        if(CompactEvidence)return await AuditSpansAsync(cues,GroundedVerification.Proposed(proposal,catalog),model,endpoint,ct);
        var episodes=GroundedVerification.Proposed(proposal,catalog);var blocks=Blocks(cues);
        var schema=JsonNode.Parse(Asset("grounded-schema.json"))!.AsObject();
        schema["properties"]!["candidates"]!["minItems"]=episodes.Count;schema["properties"]!["candidates"]!["maxItems"]=episodes.Count;
        schema["properties"]!["candidates"]!["items"]!["properties"]!["candidate"]!["maximum"]=episodes.Count-1;
        schema["properties"]!["recording"]!["properties"]!["candidates"]!["items"]!["maximum"]=episodes.Count-1;
        var source=new JsonObject{["dialogue_blocks"]=JsonSerializer.SerializeToNode(blocks.Select(b=>new{index=b.Index,text=b.SourceText})),
            ["candidates"]=JsonSerializer.SerializeToNode(episodes.Select((e,i)=>new{candidate=i,statements=GroundedVerification.Statements(e).Select((text,index)=>new{statement=index,text})}))};
        // Keep identity-bearing titles/numbers hidden while retaining the provider's
        // explicit installment structure. This is metadata, never proof of coverage.
        source["catalog_relationship"]=EpisodeCoverage.AreNumberedParts(episodes)
            ?new JsonObject{["kind"]="numbered_parts",["members"]=JsonSerializer.SerializeToNode(episodes.Select((e,i)=>new{candidate=i,part=DescriptionMapping.Part(e.Title)!.Value.Part}))}
            :new JsonObject{["kind"]="not_established"};
        var audits=new JsonArray();var results=new List<JsonObject>();
        try {for(int pass=0;pass<2;pass++) {
            source["task"]=pass==0?"Audit every guide statement against the complete original dialogue, then make ONE joint coverage decision for the recording.":"Independently audit every guide statement against the complete original dialogue, then make ONE joint coverage decision for the recording.";
            var raw=await transport.CompleteAsync(model,endpoint,Asset("grounded-prompt.txt"),source,schema,ct,$"Verifying combined episodes · pass {pass+1}/2");audits.Add(raw.DeepClone());
            results.Add(GroundedRecording.Expand(raw,blocks,episodes));
        }}catch(Exception ex){ex.Data["model_attempts"]=History(ex.Message);throw;}
        string Decision(JsonObject r)=>r.Text("assessment")+":"+r.Text("coverage")+":"+string.Join(",",r.Array("matches").Select(m=>m.Int("episode_id")))+":"+
            string.Join(",",r.Array("matches").Select(m=>EpisodeCoverage.Scope(r.Array("candidate_checks").Single(c=>c.Int("episode_id")==m.Int("episode_id")).Text("recording_scope"))));
        if(Decision(results[0])!=Decision(results[1])) {
            var error=new InvalidDataException("Independent grounded guide audits disagree. No assignment accepted.");
            error.Data["model_attempts"]=History(error.Message);throw error;
        }
        var result=results[1];result["method"]=Version;result["model"]=model;result["request_revision"]=RequestRevision;
        result["dialogue_hash"]=CollectionResolver.Hash(JsonSerializer.Serialize(cues,Data.Options));
        result["grounded_verification"]=new JsonObject{["method"]="joint-recording-guide-audits-v6",["candidate_ids"]=JsonSerializer.SerializeToNode(episodes.Select(e=>e.Id)),
            ["independent_result"]=results[0],["original_proposal"]=proposal.DeepClone()};
        return result;
        string History(string error)=>new JsonArray(audits.Select((r,i)=>(JsonNode)new JsonObject{["stage"]="grounded_candidate_audit",["pass"]=i+1,["error"]=error,["response"]=r!.DeepClone()}).ToArray()).ToJsonString();
    }

    private static bool GroundableCombination(InvalidDataException ex)=>ex.Data[AssignmentValidation.ReasonKey]?.ToString() is nameof(AssignmentIssue.NonConsecutiveEpisodes) or nameof(AssignmentIssue.OverlappingStories);
    private async Task<JsonObject> AuditRejectedCombinationAsync(InvalidDataException error,IReadOnlyList<Cue> cues,IReadOnlyList<Episode> episodes,string model,string endpoint,CancellationToken ct)
    {
        var attempts=JsonNode.Parse((string)error.Data["model_attempts"]!)!.AsArray();var proposal=attempts.Last()!["response"]!.AsObject();
        try {
            var result=await VerifySavedProposalAsync(cues,episodes,proposal,model,endpoint,ct);result["rejected_combination"]=attempts.DeepClone();return result;
        }catch(Exception ex) {
            var history=attempts.DeepClone().AsArray();
            if(ex.Data["model_attempts"] is string text)foreach(var audit in JsonNode.Parse(text)!.AsArray())history.Add(audit!.DeepClone());
            ex.Data["model_attempts"]=history.ToJsonString();throw;
        }
    }
}
