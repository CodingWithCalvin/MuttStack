using System.Text.Json.Nodes;
namespace Scout.Core;

public static class DialogueAvailability {
    // Recognize older reports without rewriting their saved evidence.
    public static string Reason(JsonObject row) => row.Text("error") switch {
        "No readable text subtitles. Speech transcription is not yet available in the .NET build." => "missing_subtitles",
        "No usable dialogue." => "empty_subtitles",
        "" when row.Text("status") == "not_processed" => row.Text("skip_reason"),
        _ => ""
    };
    public static bool Unavailable(JsonObject row) => Reason(row) is "missing_subtitles" or "empty_subtitles" or "missing_audio" or "no_speech" or "speech_not_ready" &&
        row.Array("candidates").Count == 0 && !row.Bool("source_changed") && !row.Bool("move_pending") && row.Text("relocation_error") == "" &&
        !row.Bool("conflict_review_pending") && row.Text("conflict_review_error") == "" && !row.Bool("claim_review_pending") && row.Text("claim_review_error") == "";
    public static string Message(JsonObject row) => (Reason(row) switch {
        "empty_subtitles"=>"The subtitle track contains no usable dialogue.",
        "missing_audio"=>"No readable subtitles or audio track were found.",
        "no_speech"=>"Local speech recognition found no usable dialogue.",
        "speech_not_ready"=>"The selected local speech model is not downloaded. Open Settings > Speech to prepare it.",
        _=>"No readable text subtitles were found."
    })+" File left in place.";
    public static void Mark(JsonObject row, string reason) {
        row["status"] = "not_processed"; row["skip_reason"] = reason;
        row["full_dialogue"] = false; row["candidates"] = new JsonArray();
        row.Remove("error"); row.Remove("dialogue");
    }
    // Older reports did not capture source metadata for this outcome. Explicit
    // reanalysis can retry them; ordinary issue review should not loop forever.
    public static bool SourceChanged(JsonObject row) => row["size"] != null && row["source_mtime_ns"] != null &&
        row.Text("sampled_digest") != "" && !CompilationEvidence.SourceUnchanged(row);
}
