using System.Text.Json.Nodes;

namespace Scout.Core;

/// <summary>One model decision describes the recording; candidate audits establish its evidence.</summary>
public static class GroundedRecording
{
    public static JsonObject Expand(JsonObject raw,IReadOnlyList<Block> blocks,IReadOnlyList<Episode> episodes)
    {
        if(raw.Count!=2||raw["candidates"] is not JsonArray audits||audits.Count!=episodes.Count||
           raw["recording"] is not JsonObject recording||recording.Count!=3||recording["candidates"] is not JsonArray members)
            throw new InvalidDataException("One recording-level coverage decision and every candidate audit are required.");
        var kind=recording.Text("kind");
        int count=kind switch {"single_episode" or "incomplete_episode"=>1,"continuous_parts" or "separate_episodes"=>members.Count is >=2 and <=EpisodeRange.Maximum?members.Count:-1,"unresolved"=>0,_=>-1};
        if(count<0||members.Count!=count)throw AssignmentValidation.Reject(AssignmentIssue.CoverageMismatch,"Recording coverage must select one episode, two to four episodes, or no episodes when unresolved.");
        ModelOutputQuality.ValidateText(recording["reason"],"recording coverage reason",240);
        var selected=new List<int>();
        foreach(var member in members) {
            if(member is not JsonValue value||!value.TryGetValue<int>(out var index)||index<0||index>=episodes.Count||selected.Contains(index))
                throw new InvalidDataException("Recording coverage contains an unknown or repeated candidate.");
            selected.Add(index);
        }
        var normalized=new JsonArray();var seen=new HashSet<int>();
        foreach(var node in audits) {
            if(node is not JsonObject audit||audit.Count!=5||audit.ContainsKey("recording_scope")||
               audit["candidate"] is not JsonValue value||!value.TryGetValue<int>(out var index)||index<0||index>=episodes.Count||!seen.Add(index))
                throw new InvalidDataException("Each candidate needs one evidence audit, without a separate whole-recording claim.");
            var type=EpisodeCoverage.Content(audit.Text("content_type"));
            if(selected.Contains(index)) {
                if(type is "preview_or_promo" or "recap")throw AssignmentValidation.Reject(AssignmentIssue.PromotionalContent,"Promotional or recap evidence cannot be selected in a recording assignment.");
                if(count>1&&type=="incomplete_episode")throw AssignmentValidation.Reject(AssignmentIssue.IncompleteCombinedStory,"Every selected catalog installment requires complete coverage; an incomplete installment cannot complete a combined recording.");
                if(type!=(kind=="incomplete_episode"?"incomplete_episode":"complete_episode"))
                    throw AssignmentValidation.Reject(AssignmentIssue.CoverageMismatch,"The recording decision disagrees with the selected candidate's completeness.");
            }else if(kind!="unresolved"&&(type is "complete_episode" or "incomplete_episode")&&audit["statements"] is JsonArray facts&&
                     facts.Count==GroundedVerification.Statements(episodes[index]).Length&&facts.All(f=>f.Text("verdict")=="supported"))
                throw AssignmentValidation.Reject(AssignmentIssue.CoverageMismatch,"The recording decision omits another fully supported episode candidate. Coverage remains unresolved.");
            // This is a mapping of the single explicit decision, never a repair or
            // inference from candidate count. All quotes and guide checks still pass
            // through the existing independent grounding and sequence validation.
            var copy=audit.DeepClone().AsObject();copy["recording_scope"]=!selected.Contains(index)?"not_established":kind switch {
                "single_episode"=>"entire_episode","incomplete_episode"=>"incomplete_episode",
                "continuous_parts"=>"one_of_continuous_parts",_=>"one_of_separate_episodes"};
            normalized.Add(copy);
        }
        var result=GroundedVerification.Expand(new JsonObject{["candidates"]=normalized},blocks,episodes);
        if(!result.Array("matches").Select(m=>m.Int("episode_id")).SequenceEqual(selected.Select(i=>episodes[i].Id)))
            throw AssignmentValidation.Reject(AssignmentIssue.CoverageMismatch,"The recording decision is not supported by all required guide statements and ordered original dialogue evidence.");
        result["grounded_audit"]=raw.DeepClone();result["recording_decision"]=recording.DeepClone();
        result["summary"]=recording["reason"]!.DeepClone();result["summary_source"]="recording_decision";
        foreach(var check in result.Array("candidate_checks"))check!["recording_scope_source"]="recording_decision";
        return result;
    }
}
