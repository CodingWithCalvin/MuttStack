using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core.Subtitles;

namespace Scout.Core;

public sealed record TitleCandidate(string SourceName,Episode[] Episodes,string Method);
public static class TitleCandidates {
 public const string Revision="title-candidate-verification-v1";
 public static TitleCandidate? Find(string path,IReadOnlyList<Episode> catalog) {
  var name=Path.GetFileName(path);
  if(TitleMapping.Label(name)?.Part>0)return null;
  var link=TitleMapping.Find(name,catalog,"");
  if(link?.Complete!=true||!EpisodeRange.Valid(link.Episodes)||link.Episodes.Any(e=>string.IsNullOrWhiteSpace(e.Overview)))return null;
  // Titles choose what to verify, never an assignment. Release numbers are not
  // copied into the selected provider; multipart titles must form a valid group.
  return new(name,link.Episodes,link.Method);
 }
 public static string Signature(TitleCandidate candidate,IReadOnlyList<Cue> cues,IReadOnlyList<Episode> catalog,DialogueMatcher matcher,string model,string endpoint,string generation)=>
  CollectionResolver.Hash(JsonSerializer.Serialize(new{Revision,DialogueMatcher.Version,DialogueMatcher.RequestRevision,candidate,cues,catalog,matcher.CacheScope,model,endpoint,generation},Data.Options));
 public static bool Accepted(JsonObject result,TitleCandidate candidate,IReadOnlyList<Cue> cues) {
  if(result.Text("assessment")!="supported"||result.Text("coverage")!=(candidate.Episodes.Length>1?"combined":"complete")||
   result.Text("method")!=DialogueMatcher.Version||result["title_verification"].Text("revision")!=Revision||
   result["title_verification"].Text("source_name")!=candidate.SourceName||
   !result["title_verification"].Array("candidate_ids").Select(n=>n!.GetValue<int>()).SequenceEqual(candidate.Episodes.Select(e=>e.Id))||
   !result["grounded_verification"].Bool("full_dialogue")||result.Text("dialogue_hash")!=SubtitleResult.DialogueHash(cues)||
   !result.Array("matches").Select(m=>m.Int("episode_id")).SequenceEqual(candidate.Episodes.Select(e=>e.Id)))return false;
  ModelOutputQuality.ValidateSavedResult(result);return true;
 }
}

public sealed partial class DialogueMatcher {
 public async Task<JsonObject> VerifyTitleCandidateAsync(IReadOnlyList<Cue> cues,TitleCandidate candidate,string model,string endpoint,CancellationToken ct=default) {
  ValidateEndpoint(endpoint);ct.ThrowIfCancellationRequested();
  if(cues.Count==0||!EpisodeRange.Valid(candidate.Episodes))throw new ArgumentException("Original dialogue and a valid candidate range are required.");
  foreach(var episode in candidate.Episodes)GroundedVerification.RequireDescription(episode);
  // Hide the filename, title and release numbering from the verifier. It audits
  // provider guide statements against all original dialogue, with the same
  // extractive grounding and recording-coverage checks as full identification.
  // One ordinary candidate needs one audit; multipart proposals need two.
  var result=await AuditSpansAsync(cues,candidate.Episodes,model,endpoint,ct,candidate.Episodes.Length>1?2:1);
  result["title_verification"]=JsonSerializer.SerializeToNode(new{revision=TitleCandidates.Revision,sourceName=candidate.SourceName,
   candidateIds=candidate.Episodes.Select(e=>e.Id),candidateMethod=candidate.Method,skippedCatalogSearch=true},Data.Options);
  return result;
 }
}
