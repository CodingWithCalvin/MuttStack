using System.Text.Json.Nodes;
namespace Scout.Core;

public sealed record EvidenceSection(string Heading,string Text);
public sealed record EvidenceReview(string Assessment,string Suggestion,IReadOnlyList<EvidenceSection> Sections)
{
 public static EvidenceReview Create(RecordingGroup group) {
  var assessment=group.Status switch {
   "not_processed"=>"Not processed — no readable dialogue",
   "compilation"=>"Play-all compilation — individual recordings take priority",
   "source_unavailable"=>"Source unavailable — historical suggestion only",
   "overridden_conflict"=>"Held — another recording was assigned by the user",
   "stale_human_override"=>"Human override expired — evidence or series changed",
   "unresolved_conflict"=>"Unresolved conflict — do not assign",
   "invalid_model_output"=>"Invalid model output — review required",
   "source_or_model_error"=>"Processing error — review required",
   "partial_unverified"=>"Partial episode — complementary recording not verified",
   _ when group.HumanDecision!=null&&group.Episode!=null=>"Human override — assignment forced by the user",
   _ when group.Episode!=null&&group.Rows.All(Subtitles.SubtitleResult.IsSubtitle)=>"Subtitle dialogue supports this provider assignment — suggestion",
   _ when group.Combined=>"Combined episodes — consecutive episodes in one recording",
   _ when group.Split=>"Split episode — complementary parts independently verified",
   _ when group.Feature=>"Non-episode content suggested",
   _ when group.Episode!=null&&group.Rows.All(r=>r.Text("matching_method")=="reasoning"||r["reasoning"]!=null)=>"Dialogue supports this match — suggestion",
   _ when group.Episode!=null=>"Filename/catalog suggestion — content unverified",
   _ when group.Status=="unassigned"&&group.Rows.Any(r=>r["subtitle_match"]!=null)=>group.DisplayStatus,
   _=>"Uncertain — no episode assigned"
  };
  var suggestion=group.Episode is {} episode?$"{group.Code} · {group.Title}":"No episode assignment";
  var sections=new List<EvidenceSection>();
  if(group.Compilation is {} compilation) {
   sections.Add(new("Compilation evidence",compilation.Summary));
   sections.Add(new("Contained recordings",string.Join("\n",compilation.Parts.Select(p=>$"{p.Path} ({TimeSpan.FromSeconds(p.Duration):hh\\:mm\\:ss})"))));
   sections.Add(new("Previous suggestions","Saved model suggestions and errors below are retained as history. This compilation does not reserve any episode."));
  }
  if(group.Status=="source_unavailable")sections.Add(new("Why this is held","The saved source is missing and no verified replacement was found. Its history is retained, but it does not reserve an episode or block an available recording."));
  if(group.Status=="overridden_conflict")sections.Add(new("Human conflict resolution","A forceful assignment gives this episode to another recording. This file remains held; its original model suggestions are preserved."));
  if(group.HumanDecision is {} human)sections.Add(new("Human override",human.Text("recorded_at")+"\n"+human.Text("note")+"\nThe model's original assessment below was not edited. Undo override restores automatic conflict handling."));
  if(group.Status=="unresolved_conflict")sections.Add(new("Why this is held","Different recordings compete for the same episode, or duplicate copies disagree. Individual model suggestions below are not an accepted assignment."));
  if(group.CompetingRecordings.Count>0)sections.Add(new("Competing recordings",string.Join("\n",group.CompetingRecordings)));
  if(group.Duplicate)sections.Add(new("Duplicate copies",group.DialogueCopies?$"{group.Copies} recordings have identical complete normalized dialogue and compatible durations. Encodings differ; every copy is retained under one assignment.":$"{group.Copies} identical recordings share this review and one episode assignment."));
  if(group.SplitEvidence is {} split) {
   sections.Add(new("Verified split order",$"This recording is Part {group.Part} of {group.PartCount} of {group.Code}. Exact copies of this part are grouped separately from the other part."));
   sections.Add(new("Continuity verification",split["verification"].Text("continuity")));
   sections.Add(new("Why these are parts, not duplicates",split["verification"].Text("reason")));
  }
  foreach(var row in group.Rows) {
   sections.Add(new("Recording",row.Text("path")));
   if(row["transcription"] is JsonObject speech)sections.Add(new("Machine transcription",$"{speech.Text("name")} · {speech.Text("device")} · audio track {speech.Int("audio_track")} · English\nGenerated locally from audio. Recognition errors are possible; model scores are not confidence in an episode assignment."));
   if(row["subtitle_match"] is JsonObject subtitle) {
    bool current=Subtitles.SubtitleResult.IsSubtitle(row);
    sections.Add(new(current?"Subtitle matching":row["reasoning"]!=null?"Subtitle stage before AI":"Subtitle comparison",subtitle.Text("reason")+"\n"+subtitle.Text("note")));
    if(subtitle["source_title_evidence"] is JsonObject sourceTitle)
     sections.Add(new("Corroborated source title",sourceTitle.Text("name")+"\nUnique provider title supported by a complete subtitle match, "+(sourceTitle.Array("title_terms").Count==0?"independently ranked plot details: ":"repeated title terms ("+string.Join(", ",sourceTitle.Array("title_terms").Select(n=>n!.GetValue<string>()))+") and distinctive synopsis terms: ")+string.Join(", ",sourceTitle.Array("synopsis_terms").Select(n=>n!.GetValue<string>()))+". The filename is a hint for this recording only."));
    if(subtitle.Array("equivalent_references").Count>0)sections.Add(new("Duplicate subtitle versions",$"{subtitle.Array("equivalent_references").Count} equivalent reference version(s) grouped by full dialogue. Different upload numbers do not establish additional episode identities."));
    if(subtitle["best"] is JsonObject best) {
     sections.Add(new("Subtitle references",string.Join("\n",best.Array("references").Select(r=>r.Text("name")+"\n"+r.Text("url")))));
     sections.Add(new("Provider mapping",subtitle.Text("provider").ToUpperInvariant()+" series "+subtitle.Text("series_id")+"\n"+best["link"].Text("method").Replace('-',' ')+"\nUpload numbering is not used as provider numbering."));
     if(best["overlap"] is JsonObject overlap)sections.Add(new("Dialogue correspondence",$"{overlap["source_coverage"]!.GetValue<double>():P1} of source sequences; {overlap["reference_coverage"]!.GetValue<double>():P1} of reference sequences. These are text-overlap measurements, not model confidence."));
     if(best.Array("parts").Count>0)sections.Add(new("Combined subtitle references","Separate, substantial dialogue portions occur in the expected order. Short previews and repeated parts do not qualify."));
    }
   }
   if(group.NotProcessed)sections.Add(new("Not processed",DialogueAvailability.Message(row)+" Reanalyze after adding readable subtitles or preparing local speech recognition."));
   else if(!string.IsNullOrWhiteSpace(row.Text("error")))sections.Add(new("Processing error",row.Text("error")));
   if(row.Bool("move_pending"))sections.Add(new("Move needs reconciliation","Generate the move preview to verify the destination of an interrupted move. No assignment or further move is accepted until its location is verified."));
   if(row.Text("relocation_error")!="")sections.Add(new("File location unresolved",row.Text("relocation_error")));
   if(row.Array("path_history").Count>0)sections.Add(new("Verified path history",string.Join("\n",row.Array("path_history").Select(h=>h.Text("from")+" → "+h.Text("to")))));
   if(row.Bool("source_changed"))sections.Add(new("Source changed","This file changed during processing. Its previous suggestion cannot be used."));
   if(row.Text("conflict_review_error")!="")sections.Add(new("Automated conflict review",row.Text("conflict_review_error")));
   if(row.Bool("conflict_review_pending"))sections.Add(new("Automated conflict review","Paused or incomplete. Resume Scout to finish checking all competing recordings."));
   if(row["conflict_review"]!=null)sections.Add(new("Automated conflict review","Re-evaluated against the full episode guide with two independent dialogue checks. Collection uniqueness rules still apply."));
   if(row["claim_resolution"] is {} resolution)sections.Add(new("Focused claim resolution",resolution.Text("summary","Individual guide checks are saved here. See pending status or errors below before accepting an assignment.")));
   if(row.Bool("claim_review_pending"))sections.Add(new("Focused review pending","Resume Scout to finish the saved individual claim checks. No assignment is accepted while this check is incomplete."));
   if(row.Text("claim_review_error")!="")sections.Add(new("Focused review error",row.Text("claim_review_error")));
   if(row.Text("issue_review_reason")!="")sections.Add(new("Automated issue review","Included because: "+row.Text("issue_review_reason").Replace('_',' ')+". The current outcome is shown above; incomplete or inconclusive checks remain held."));
   if(row["title_candidate"] is {} titleHint)sections.Add(new("Episode-title candidate",titleHint.Text("source_name")+"\n"+
    string.Join(" / ",titleHint.Array("episodes").Select(e=>e.Text("title")))+"\nThe filename proposes a candidate only; dialogue verification is required. AI-off runs retain this hint without calling a model."));
   if(row["title_verification_attempt"] is {} titleAttempt)sections.Add(new("Title verification",titleAttempt.Text("reason")));
   var reasoning=row["reasoning"];
   if(reasoning?["title_verification"]!=null)sections.Add(new("Focused guide check","The model checked the proposed guide statements against complete original dialogue, without seeing the filename or episode title. Whole-catalog identification was skipped; collection checks still apply."));
   if(reasoning?["grounded_verification"] is JsonObject auditScope&&auditScope.ContainsKey("full_dialogue")) {
    sections.Add(new("Dialogue examined",auditScope.Bool("full_dialogue")?"Complete original dialogue.":$"Selected original passages: {auditScope.Int("included_blocks")} of {auditScope.Int("total_blocks")} blocks. Unseen passages were not verified; this remains a model-supported suggestion."));
    if(auditScope.Text("fallback_reason")!="")sections.Add(new("Full-dialogue fallback",auditScope.Text("fallback_reason")));
   }
   if(reasoning.Text("coverage")!="")sections.Add(new("Recording coverage",reasoning.Text("coverage")));
   if(reasoning.Text("request_adjustments")!="")sections.Add(new("Request recovery",reasoning.Text("request_adjustments")));
   var modelAssessment=reasoning.Text("assessment");
   if(modelAssessment!="")sections.Add(new(Subtitles.SubtitleResult.IsSubtitle(row)?"Subtitle assessment":"Model assessment",modelAssessment switch {"supported"=>"Supported by dialogue (not a guarantee of correctness)","uncertain"=>"Uncertain — insufficient evidence","non_episode"=>"Likely non-episode content",_=>modelAssessment}));
   if(reasoning?["recording_decision"] is JsonObject recording)sections.Add(new("Recording coverage decision",recording.Text("kind").Replace('_',' ')+"\n"+recording.Text("reason")));
   else if(reasoning.Text("summary")!="")sections.Add(new("Dialogue summary",reasoning.Text("summary")));
   var cues=row["dialogue"].Array("cues").Select(n=>new Cue(n?["start"]?.GetValue<double>()??0,n.Text("text"))).ToList();
   var blocks=DialogueMatcher.Blocks(cues);
   if(group.SplitEvidence is {} proof) {
    var key=proof.Text("fingerprint_a")==CollectionResolver.FingerprintKey(row["primary_fingerprint"])?"evidence_a":"evidence_b";
    foreach(var index in proof["verification"].Array(key)) {
     var block=blocks.FirstOrDefault(b=>b.Index==index!.GetValue<int>());
     if(block!=null)sections.Add(new("Split continuity evidence · "+Time(block.Start),block.SourceText));
    }
   }
   foreach(var candidate in row.Array("candidates")) {
    var e=candidate?["episode"] is {} node?Data.Episode(node):null;
    sections.Add(new("Suggested episode",e==null?"No episode details saved":$"{e.Code} · {e.Title}"));
    if(candidate.Text("reason")!="")sections.Add(new("Why it was suggested",candidate.Text("reason")));
    if(e?.Overview is {Length:>0} overview)sections.Add(new("Episode guide",overview));
    if(e?.DescriptionSource is {} description)sections.Add(new("Description source",$"{description.Provider.ToUpperInvariant()} · episode {description.EpisodeId}\n{description.Url}\nSupplemental description only; the selected catalog controls episode identity and numbering."));
    if(e?.OriginalDescription is {} original)sections.Add(new("Replaced guide description",original.Overview+"\nThis description matched a different episode more closely. The replacement retains the selected catalog's episode ID and numbering."));
    var quotes=candidate.Array("evidence");
    foreach(var quote in quotes) {
     var start=quote?["start"]?.GetValue<double>()??0;var text=quote.Text("text",quote.Text("source_text"));
     // Only expand a block when the original cue text exactly matches the saved evidence.
     var block=blocks.FirstOrDefault(b=>Math.Abs(b.Start-start)<0.01&&b.SourceText==text);
     var body=block==null?text:string.Join("\n",cues.Skip(block.CueIndex).Take(block.EndCueIndex-block.CueIndex+1).Select(c=>$"[{Time(c.Start)}] {c.Text}"));
     sections.Add(new($"Supporting dialogue · {Time(start)}",body));
    }
    if(quotes.Count==0)sections.Add(new("Dialogue evidence","No dialogue excerpts saved for this suggestion. Filename/catalog matching does not verify video content."));
   }
   if(reasoning?["shared_guide_grounding"] is JsonObject sharedGuide) {
    sections.Add(new("Shared multipart guide",reasoning?["whole_work_audit"]!=null?
     "The provider repeats one synopsis across the numbered parts. The complete continuous work was verified using its opening, development and ending, with measured runtime corroboration. No exact installment cut point is asserted.":
     "The provider repeats one synopsis across the numbered parts. The whole-story guide was checked once; see the saved installment evidence and coverage decision."));
    foreach(var stage in reasoning.Array("whole_work_evidence"))sections.Add(new("Whole-story "+stage.Text("stage"),stage.Text("source_quote")));
    foreach(var fact in sharedGuide.Array("candidate_checks").SelectMany(c=>c.Array("guide_grounding")))
     sections.Add(new("Shared guide statement · "+fact.Text("verdict").Replace('_',' '),fact.Text("guide_text")+"\n"+
      string.Join("\n",fact.Array("evidence").Select(q=>q.Text("source_quote",q.Text("quote"))))));
   }
   foreach(var check in reasoning.Array("candidate_checks")) {
    sections.Add(new("Candidate check · "+check.Text("episode_title",$"Episode ID {check.Int("episode_id")}"),check.Text("event_comparison","No comparison explanation saved.")));
    if(check.Text("content_type")!="")sections.Add(new("Source context",EpisodeCoverage.Content(check.Text("content_type")).Replace('_',' ')));
    if(check.Text("recording_scope")!="")sections.Add(new("Episode coverage",EpisodeCoverage.Scope(check.Text("recording_scope")).Replace('_',' ')));
    foreach(var fact in check.Array("guide_grounding")) {
     var text=fact.Text("guide_text");
     foreach(var quote in fact.Array("evidence")) {
      var block=blocks.FirstOrDefault(b=>b.Index==quote.Int("block",-1));
      text+="\n"+(block==null?"":$"[{Time(block.Start)}] ")+quote.Text("source_quote",quote.Text("quote"));
     }
     sections.Add(new("Guide statement · "+fact.Text("verdict").Replace('_',' '),text));
    }
    foreach(var id in check.Array("contradicting_blocks")) {
     var block=blocks.FirstOrDefault(b=>b.Index==id?.GetValue<int>());
     sections.Add(new("Contradicting dialogue",block==null?$"Source block {id} — original dialogue unavailable.":$"[{Time(block.Start)}] {block.SourceText}"));
    }
   }
   foreach(var alternative in reasoning.Array("alternatives")) {
    var id=alternative.Int("episode_id");var title=reasoning.Array("candidate_checks").FirstOrDefault(c=>c.Int("episode_id")==id).Text("episode_title",$"Episode ID {id}");
    sections.Add(new("Rejected alternative · "+title,alternative.Text("reason_against",alternative.Text("reason","No rejection explanation saved."))));
   }
   if(row.Array("candidates").Count==0&&reasoning==null&&row.Text("error")==""&&row["dialogue"].Array("cues").Count==0&&row["subtitle_match"]==null)sections.Add(new("Evidence unavailable","This recording has no saved match rationale or dialogue evidence."));
  }
  return new(assessment,suggestion,sections);
 }
 private static string Time(double seconds)=>TimeSpan.FromSeconds(double.IsFinite(seconds)?Math.Max(0,seconds):0).ToString(@"hh\:mm\:ss");
}
