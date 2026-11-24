using System.Text.Json.Nodes;

namespace Scout.Core;

/// <summary>A duplicated whole-story synopsis cannot describe each installment independently.</summary>
public static class SharedGuideGrounding
{
    public static JsonObject Schema(JsonObject template,IReadOnlyList<DialogueSpan> spans,IReadOnlyList<Episode> episodes) {
        if(!EpisodeCoverage.HasSharedDescription(episodes))throw new InvalidDataException("A shared guide requires consecutive numbered parts with the same description.");
        var schema=CompactGrounding.Schema(template,spans,[episodes[0]]);
        var parts=new JsonObject();
        for(int i=0;i<episodes.Count;i++)parts["c"+i]=new JsonObject{
            ["type"]="array",["minItems"]=0,["maxItems"]=4,
            ["items"]=new JsonObject{["type"]="string",["enum"]=new JsonArray(spans.Select(s=>(JsonNode?)JsonValue.Create("s"+s.Id)).ToArray())}};
        schema["properties"]!["installments"]=new JsonObject{["type"]="object",["additionalProperties"]=false,["properties"]=parts,
            ["required"]=new JsonArray(parts.Select(p=>(JsonNode?)JsonValue.Create(p.Key)).ToArray())};
        schema.Array("required").Add("installments");
        JsonObject Decision(bool complete)=>new(){["type"]="object",["additionalProperties"]=false,
            ["required"]=new JsonArray("kind","candidates"),["properties"]=new JsonObject{
                ["kind"]=new JsonObject{["type"]="string",["enum"]=new JsonArray(complete?"continuous_parts":"unresolved")},
                ["candidates"]=new JsonObject{["type"]="array",["minItems"]=complete?episodes.Count:0,["maxItems"]=complete?episodes.Count:0,
                    ["items"]=new JsonObject{["type"]="string",["enum"]=new JsonArray(Enumerable.Range(0,episodes.Count).Select(i=>(JsonNode?)JsonValue.Create("c"+i)).ToArray())}}}};
        schema["properties"]!["recording"]=new JsonObject{["anyOf"]=new JsonArray(Decision(true),Decision(false))};
        return schema;
    }

    public static JsonObject Expand(JsonObject raw,IReadOnlyList<DialogueSpan> spans,IReadOnlyList<Block> blocks,IReadOnlyList<Episode> episodes) {
        if(!EpisodeCoverage.HasSharedDescription(episodes)||!EpisodeRange.Valid(episodes)||raw.Count!=3||
            raw["installments"] is not JsonObject installments||installments.Count!=episodes.Count||
            raw["recording"] is not JsonObject recording||recording.Count!=2||recording["candidates"] is not JsonArray members)
            throw new InvalidDataException("Invalid shared-guide recording audit.");
        bool complete=recording.Text("kind")=="continuous_parts";
        if(!complete&&recording.Text("kind")!="unresolved"||
            !members.Select(n=>n?.GetValue<string>()).SequenceEqual(complete?Enumerable.Range(0,episodes.Count).Select(i=>"c"+i):[]))
            throw AssignmentValidation.Reject(AssignmentIssue.CoverageMismatch,"A shared synopsis requires the complete ordered catalog group or an unresolved decision.");
        // Audit the whole-story guide ONCE. Never pretend that its events belong to
        // two independent stories, nor silently copy its citations into each part.
        var whole=new JsonObject{["candidates"]=raw["candidates"]?.DeepClone(),["recording"]=new JsonObject{
            ["kind"]=complete?"single_episode":"unresolved",["candidates"]=complete?new JsonArray("c0"):new JsonArray()}};
        CompactGrounding.ValidateVisibleCitations(whole,spans);
        var guide=CompactGrounding.ExpandKeyed(whole,spans,blocks,[episodes[0]]);
        var checks=new JsonArray();var proof=new JsonArray();int previous=-1;
        double lastTime=blocks.Count==0?0:blocks[^1].Start;
        for(int i=0;i<episodes.Count;i++) {
            if(installments["c"+i] is not JsonArray ids||ids.Count>4)throw new InvalidDataException("Every installment requires its evidence slot.");
            var cited=new List<int>();var quotes=new JsonArray();var seen=new HashSet<int>();
            foreach(var node in ids) {
                if(node is not JsonValue value||!value.TryGetValue<string>(out var label)||label.Length<2||label[0]!='s'||
                    !int.TryParse(label.AsSpan(1),out int id)||label!="s"+id||id<0||id>=spans.Count||!seen.Add(id))
                    throw new InvalidDataException("Unknown or repeated installment evidence span.");
                var span=spans[id];var quote=DialogueEvidence.Citation(spans,id,blocks);
                if(!cited.Contains(span.Block))cited.Add(span.Block);
                quotes.Add(new JsonObject{["span_id"]=label,["block"]=span.Block,["source_quote"]=quote});
            }
            if(complete) {
                if(cited.Count<2||!cited.SequenceEqual(cited.Order())||cited[0]<=previous)
                    throw AssignmentValidation.Reject(AssignmentIssue.OverlappingStories,"Each installment needs at least two distinct, successive original blocks in playback order.");
                // Distributed evidence is an additional guard against a brief recap,
                // a promo or arbitrarily dividing a few lines. It never establishes identity.
                if(lastTime<=0||blocks[cited[^1]].Start-blocks[cited[0]].Start<lastTime*.5/episodes.Count||
                    i==0&&blocks[cited[0]].Start>lastTime*.2||i==episodes.Count-1&&blocks[cited[^1]].Start<lastTime*.8)
                    throw AssignmentValidation.Reject(AssignmentIssue.UncertainStoryRole,"Shared-guide evidence does not cover the successive installments across the recording.");
                previous=cited[^1];
            }
            checks.Add(new JsonObject{["episode_id"]=episodes[i].Id,["verdict"]=complete?"supported":"insufficient_evidence",
                ["supporting_blocks"]=new JsonArray(cited.Select(b=>(JsonNode?)JsonValue.Create(b)).ToArray()),["contradicting_blocks"]=new JsonArray(),
                ["content_type"]=complete?"complete_episode":"unknown",
                ["reason"]=complete?"The shared story guide is supported, with distinct ordered evidence for this complete installment.":"The shared guide does not establish the complete installment range."});
            proof.Add(new JsonObject{["episode_id"]=episodes[i].Id,["evidence"]=quotes});
        }
        var result=CompactVerification.Expand(new JsonObject{["assessment"]=complete?"supported":"uncertain",["coverage"]=complete?"combined":"unknown",
            ["checks"]=checks,["note"]=complete?"The complete numbered story is supported by its shared synopsis and separate installment evidence.":"Complete coverage of this shared-guide story remains unresolved."},blocks,episodes);
        DialogueMatcher.Validate(result,blocks,episodes,true,requireContentType:true);
        foreach(var check in result.Array("candidate_checks"))check!["recording_scope"]=complete?"one_of_continuous_parts":"not_established";
        result["recording_decision"]=recording.DeepClone();result["shared_guide_grounding"]=guide;
        result["installment_evidence"]=proof;result["shared_guide_audit"]=raw.DeepClone();
        return result;
    }
}
