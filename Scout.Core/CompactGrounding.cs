using System.Text.Json.Nodes;

namespace Scout.Core;

/// <summary>Expands source IDs into exact original quotes, without changing model verdicts.</summary>
public static class CompactGrounding
{
    public static void ValidateVisibleCitations(JsonObject raw,IReadOnlyList<DialogueSpan> visible) {
        var allowed=visible.Select(s=>"s"+s.Id).ToHashSet();
        if(raw["candidates"] is not JsonObject candidates)throw new InvalidDataException("Missing fixed candidate slots.");
        foreach(var candidate in candidates) {
            if(candidate.Value?["statements"] is not JsonObject facts)throw new InvalidDataException("Missing fixed statement slots.");
            foreach(var fact in facts) {
                if(fact.Value?["evidence"] is not JsonArray citations)throw new InvalidDataException("Missing evidence array.");
                foreach(var citation in citations)if(citation is not JsonValue value||!value.TryGetValue<string>(out var id)||!allowed.Contains(id))
                    throw new InvalidDataException("Evidence cites original dialogue that was not supplied to this audit.");
            }
        }
    }
    public static JsonObject Schema(JsonObject template,IReadOnlyList<DialogueSpan> spans,IReadOnlyList<Episode> episodes) {
        var fact=template["properties"]!["candidates"]!["items"]!["properties"]!["statements"]!["items"]!.DeepClone().AsObject();
        fact["properties"]!.AsObject().Remove("statement");fact["required"]=new JsonArray("verdict","evidence");
        fact["properties"]!["evidence"]!["items"]!["enum"]=new JsonArray(spans.Select(s=>(JsonNode?)JsonValue.Create("s"+s.Id)).ToArray());
        var role=template["properties"]!["candidates"]!["items"]!["properties"]!["role"]!;
        static JsonObject RequiredObject(JsonObject properties)=>new(){["type"]="object",["additionalProperties"]=false,["properties"]=properties,
            ["required"]=new JsonArray(properties.Select(p=>(JsonNode?)JsonValue.Create(p.Key)).ToArray())};
        var candidates=new JsonObject();
        for(int i=0;i<episodes.Count;i++) {
            var facts=new JsonObject();for(int j=0;j<GroundedVerification.Statements(episodes[i]).Length;j++)facts["g"+j]=fact.DeepClone();
            candidates["c"+i]=RequiredObject(new JsonObject{["statements"]=RequiredObject(facts),["role"]=role.DeepClone()});
        }
        var schema=template.DeepClone().AsObject();schema["properties"]!["candidates"]=RequiredObject(candidates);
        var member=new JsonObject{["type"]="string",["enum"]=new JsonArray(Enumerable.Range(0,episodes.Count).Select(i=>(JsonNode?)JsonValue.Create("c"+i)).ToArray())};
        JsonObject Coverage(int count,params string[] kinds)=>RequiredObject(new JsonObject{
            ["candidates"]=new JsonObject{["type"]="array",["minItems"]=count,["maxItems"]=count,["items"]=member.DeepClone()},
            ["kind"]=new JsonObject{["type"]="string",["enum"]=new JsonArray(kinds.Select(k=>(JsonNode?)JsonValue.Create(k)).ToArray())}});
        var variants=new JsonArray(Coverage(1,"single_episode","incomplete_episode"),Coverage(0,"unresolved"));
        for(int count=2;count<=Math.Min(episodes.Count,EpisodeRange.Maximum);count++)variants.Add(Coverage(count,"continuous_parts","separate_episodes"));
        // Constrain the generated shape instead of repairing an inconsistent
        // recording decision after inference. Semantic and uniqueness checks remain.
        schema["properties"]!["recording"]=new JsonObject{["anyOf"]=variants};
        return schema;
    }

    public static JsonObject ExpandKeyed(JsonObject raw,IReadOnlyList<DialogueSpan> spans,IReadOnlyList<Block> blocks,IReadOnlyList<Episode> episodes) {
        if(raw.Count!=2||raw["candidates"] is not JsonObject candidates||candidates.Count!=episodes.Count||raw["recording"] is not JsonObject recording||recording.Count!=2||recording["candidates"] is not JsonArray members)
            throw new InvalidDataException("Every candidate requires its fixed response slot and one recording decision.");
        var audits=new JsonArray();
        for(int i=0;i<episodes.Count;i++) {
            int count=GroundedVerification.Statements(episodes[i]).Length;
            if(candidates["c"+i] is not JsonObject audit||audit.Count!=2||!audit.ContainsKey("role")||audit["statements"] is not JsonObject facts||facts.Count!=count)
                throw new InvalidDataException("Every guide statement requires exactly one fixed response slot.");
            var statements=new JsonArray();
            for(int j=0;j<count;j++) {
                if(facts["g"+j] is not JsonObject fact||fact.Count!=2||!fact.ContainsKey("verdict")||!fact.ContainsKey("evidence"))
                    throw new InvalidDataException("Missing or unknown guide response slot.");
                statements.Add(new JsonObject{["statement"]=j,["verdict"]=fact["verdict"]?.DeepClone(),["evidence"]=fact["evidence"]?.DeepClone()});
            }
            audits.Add(new JsonObject{["candidate"]=i,["statements"]=statements,["role"]=audit["role"]?.DeepClone()});
        }
        var selected=new JsonArray();
        foreach(var node in members) {
            if(node is not JsonValue value||!value.TryGetValue<string>(out var label)||label.Length<2||label[0]!='c'||!int.TryParse(label.AsSpan(1),out int id)||label!="c"+id||id<0||id>=episodes.Count)
                throw new InvalidDataException("Unknown fixed candidate response slot.");
            selected.Add(id);
        }
        var result=Expand(new JsonObject{["candidates"]=audits,["recording"]=new JsonObject{["kind"]=recording["kind"]?.DeepClone(),["candidates"]=selected}},spans,blocks,episodes);
        result["compact_grounded_decision"]=raw.DeepClone();
        return result;
    }

    public static JsonObject Expand(JsonObject raw,IReadOnlyList<DialogueSpan> spans,IReadOnlyList<Block> blocks,IReadOnlyList<Episode> episodes) {
        if(raw.Count!=2||raw["candidates"] is not JsonArray candidates||raw["recording"] is not JsonObject decision||decision.Count!=2||decision["candidates"] is not JsonArray members)
            throw new InvalidDataException("Invalid compact grounded decision.");
        var audits=new JsonArray();var contexts=new JsonArray();
        foreach(var node in candidates) {
            if(node is not JsonObject candidate||candidate.Count!=3||candidate["statements"] is not JsonArray statements||!candidate.ContainsKey("role")||!candidate.ContainsKey("candidate"))
                throw new InvalidDataException("A compact audit requires candidate, statements and role.");
            // Completeness comes solely from the explicit joint recording decision.
            // Old responses with per-candidate completeness are rejected, never repaired.
            var role=candidate.Text("role");
            if(role is not ("main_story" or "preview_or_promo" or "recap" or "unknown"))
                throw new InvalidDataException("Unknown candidate evidence role.");
            bool selected=members.Any(m=>JsonNode.DeepEquals(m,candidate["candidate"]));
            var content=role=="main_story"?(selected&&decision.Text("kind")=="incomplete_episode"?"incomplete_episode":"complete_episode"):role;
            var facts=new JsonArray();var story=new List<int>();
            foreach(var nodeFact in statements) {
                if(nodeFact is not JsonObject fact||fact.Count!=3||fact["evidence"] is not JsonArray ids||ids.Count>2)
                    throw new InvalidDataException("Invalid compact guide evidence.");
                var evidence=new JsonArray();var seen=new HashSet<int>();
                foreach(var idNode in ids) {
                    if(idNode is not JsonValue v||!v.TryGetValue<string>(out var label)||label.Length<2||label[0]!='s'||!int.TryParse(label.AsSpan(1),out var id)||label!="s"+id||id<0||id>=spans.Count||!seen.Add(id))
                        throw new InvalidDataException("Unknown or repeated original evidence span.");
                    var span=spans[id];var quote=DialogueEvidence.Citation(spans,id,blocks);
                    evidence.Add(new JsonObject{["block"]=span.Block,["quote"]=quote});
                    if(quote!=span.Text)contexts.Add(new JsonObject{["candidate"]=candidate["candidate"]?.DeepClone(),["statement"]=fact["statement"]?.DeepClone(),
                        ["span_id"]="s"+id,["context_span_id"]="s"+(id-1),["context_also_cited"]=ids.Any(n=>n is JsonValue token&&token.TryGetValue<string>(out var previous)&&previous=="s"+(id-1)),["selected_text"]=span.Text,["original_context"]=quote,["block"]=span.Block});
                    if(fact.Text("verdict")=="supported"&&!story.Contains(span.Block))story.Add(span.Block);
                }
                facts.Add(new JsonObject{["statement"]=fact["statement"]?.DeepClone(),["verdict"]=fact["verdict"]?.DeepClone(),["evidence"]=evidence});
            }
            audits.Add(new JsonObject{["candidate"]=candidate["candidate"]?.DeepClone(),["statements"]=facts,["content_type"]=content,
                ["story_blocks"]=new JsonArray(story.Take(4).Select(i=>(JsonNode?)JsonValue.Create(i)).ToArray()),
                ["reason"]="Guide statements were checked against the cited original dialogue. See individual statement verdicts."});
        }
        var recording=decision.DeepClone().AsObject();recording["reason"]=decision.Text("kind") switch {
            "single_episode"=>"The model identifies one complete catalog episode covering this recording.",
            "continuous_parts"=>"The model identifies complete consecutive catalog installments forming a continuous story.",
            "separate_episodes"=>"The model identifies separate complete episodes in playback order.",
            "incomplete_episode"=>"The model identifies an incomplete recording of one catalog episode.",
            _=>"The model could not establish a complete recording assignment from the guide evidence."};
        var result=GroundedRecording.Expand(new JsonObject{["candidates"]=audits,["recording"]=recording},blocks,episodes);
        if(contexts.Count>0)result["citation_context"]=contexts;
        result["compact_grounded_decision"]=raw.DeepClone();result["explanation_source"]="generated_from_model_verdicts_and_original_source_ids";
        return result;
    }
}
