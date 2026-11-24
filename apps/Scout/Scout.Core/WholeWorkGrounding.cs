using System.Text.Json;
using System.Text.Json.Nodes;
namespace Scout.Core;

// A continuous movie can be a complete numbered catalog group without exposing
// the distributor's cut point. Store whole-work evidence explicitly; never
// manufacture separate installment quotations or infer identity from runtime.
public static class WholeWorkGrounding {
 public static JsonObject Schema(JsonObject template,IReadOnlyList<DialogueSpan> spans,IReadOnlyList<Episode> episodes) {
  var schema=SharedGuideGrounding.Schema(template,spans,episodes);
  schema["properties"]!.AsObject().Remove("installments");
  var stages=new JsonObject();
  foreach(var stage in new[]{"opening","development","ending"})stages[stage]=new JsonObject{
   ["type"]="array",["minItems"]=0,["maxItems"]=1,["items"]=new JsonObject{["type"]="string",
    ["enum"]=new JsonArray(spans.Select(s=>(JsonNode?)JsonValue.Create("s"+s.Id)).ToArray())}};
  schema["properties"]!["story_extent"]=new JsonObject{["type"]="object",["additionalProperties"]=false,
   ["properties"]=stages,["required"]=new JsonArray("opening","development","ending")};
  schema["required"]=new JsonArray("candidates","story_extent","recording");return schema;
 }
 public static JsonObject Expand(JsonObject raw,IReadOnlyList<DialogueSpan> spans,IReadOnlyList<Block> blocks,IReadOnlyList<Episode> episodes,RecordingExtent extent) {
  if(!EpisodeCoverage.HasSharedDescription(episodes)||!EpisodeRange.Valid(episodes)||!extent.Supports(episodes.Count)||
   raw.Count!=3||raw["story_extent"] is not JsonObject stages||stages.Count!=3||
   raw["recording"] is not JsonObject recording||recording.Count!=2||recording["candidates"] is not JsonArray members)
   throw new InvalidDataException("Invalid complete-work audit or missing measured runtime corroboration.");
  bool complete=recording.Text("kind")=="continuous_parts";
  if(!complete&&recording.Text("kind")!="unresolved"||!members.Select(n=>n?.GetValue<string>()).SequenceEqual(complete?Enumerable.Range(0,episodes.Count).Select(i=>"c"+i):[]))
   throw new InvalidDataException("A complete-work audit must select the entire ordered catalog group or abstain.");
  var whole=new JsonObject{["candidates"]=raw["candidates"]?.DeepClone(),["recording"]=new JsonObject{
   ["kind"]=complete?"single_episode":"unresolved",["candidates"]=complete?new JsonArray("c0"):new JsonArray()}};
  CompactGrounding.ValidateVisibleCitations(whole,spans);
  var guide=CompactGrounding.ExpandKeyed(whole,spans,blocks,[episodes[0]]);
  var evidence=new JsonArray();var cited=new List<int>();
  foreach(var stage in new[]{"opening","development","ending"}) {
   if(stages[stage] is not JsonArray ids||ids.Count>1||complete&&ids.Count!=1)throw new InvalidDataException("Each complete-work stage requires one original span.");
   foreach(var node in ids) {
    if(node is not JsonValue value||!value.TryGetValue<string>(out var label)||label.Length<2||label[0]!='s'||
     !int.TryParse(label.AsSpan(1),out int id)||label!="s"+id||id<0||id>=spans.Count)throw new InvalidDataException("Unknown original whole-work evidence span.");
    var span=spans[id];var quote=DialogueEvidence.Citation(spans,id,blocks);
    if(complete) {
     double fraction=blocks[span.Block].Start/extent.DurationSeconds;
     if(stage=="opening"&&fraction>.2||stage=="development"&&(fraction<.3||fraction>.7)||stage=="ending"&&fraction<.8||
      cited.Count>0&&span.Block<=cited[^1])throw new InvalidDataException("Whole-work evidence must cover the opening, development and ending in playback order.");
    }
    cited.Add(span.Block);evidence.Add(new JsonObject{["stage"]=stage,["span_id"]=label,["block"]=span.Block,["source_quote"]=quote});
   }
  }
  if(complete&&(blocks.Count<3||blocks[0].Start>extent.DurationSeconds*.1||blocks[^1].Start<extent.DurationSeconds*.85||blocks[^1].Start>extent.DurationSeconds))
   throw new InvalidDataException("Full dialogue does not span the measured recording.");
  string reason=complete?"Complete continuous story verified against the shared guide, with opening, development and ending evidence and corroborating measured runtime. Catalog parts describe the whole work; no exact cut point is asserted.":"The shared story was checked, but complete coverage of its catalog parts remains unresolved.";
  var result=new JsonObject{["assessment"]=complete?"supported":"uncertain",["coverage"]=complete?"combined":"unknown",["summary"]=reason,
   ["matches"]=new JsonArray(),["candidate_checks"]=new JsonArray(),["alternatives"]=new JsonArray(),
   ["shared_guide_grounding"]=guide,["recording_decision"]=recording.DeepClone(),["whole_work_audit"]=raw.DeepClone(),
   ["recording_extent"]=JsonSerializer.SerializeToNode(extent,Data.Options),["whole_work_evidence"]=evidence};
  foreach(var episode in episodes) {
   result.Array("candidate_checks").Add(new JsonObject{["episode_title"]=episode.Title,["episode_id"]=episode.Id,["guide_event"]=episode.Overview,
    ["supporting_blocks"]=JsonSerializer.SerializeToNode(cited.Distinct()),["contradicting_blocks"]=new JsonArray(),["event_comparison"]=reason,
    ["content_type"]=complete?"complete_episode":"unknown",["verdict"]=complete?"supported":"insufficient_evidence",
    ["recording_scope"]=complete?"one_of_continuous_parts":"not_established"});
   if(complete)result.Array("matches").Add(new JsonObject{["episode_title"]=episode.Title,["episode_id"]=episode.Id,["reason"]=reason,
    ["evidence"]=JsonSerializer.SerializeToNode(cited.Select(i=>blocks[i]),Data.Options)});
  }
  return result;
 }
 // Called before the ordinary validator relaxes its separate-story overlap rule.
 public static bool ValidProof(JsonObject result,IReadOnlyList<Block> blocks,IReadOnlyList<Episode> episodes) {
  if(result["whole_work_audit"] is not JsonObject raw||result["recording_extent"]?.Deserialize<RecordingExtent>(Data.Options) is not {} extent)return false;
  var proof=Expand(raw,DialogueEvidence.Spans(blocks),blocks,episodes,extent);
  return proof.Text("assessment")=="supported"&&
   proof.Array("matches").Select(m=>m.Int("episode_id")).SequenceEqual(result.Array("matches").Select(m=>m.Int("episode_id")))&&
   JsonNode.DeepEquals(proof["candidate_checks"],result["candidate_checks"]);
 }
 public static bool CoverageOnly(JsonObject result,IReadOnlyList<Episode> episodes)=>EpisodeCoverage.HasSharedDescription(episodes)&&
  result.Text("assessment")=="uncertain"&&result["shared_guide_grounding"] is JsonObject guide&&
  guide.Array("candidate_checks").Count==1&&guide.Array("candidate_checks")[0].Text("content_type")=="complete_episode"&&
  guide.Array("candidate_checks")[0].Array("guide_grounding").Count>0&&
  guide.Array("candidate_checks")[0].Array("guide_grounding").All(g=>g.Text("verdict")=="supported");
}
