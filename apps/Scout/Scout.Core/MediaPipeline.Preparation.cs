using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace Scout.Core;

public sealed partial class MediaPipeline {
    static readonly string[] EvidenceFields = ["duration", "size", "source_mtime_ns", "sampled_digest", "dialogue", "transcription"];
    // Read subtitle tracks before inference so a play-all title listed first cannot
    // monopolize the model before its individual recordings are discovered.
    async Task<JsonObject> PrepareAsync(string path, JsonObject? cached, MatchSettings settings, IProgress<string> progress, CancellationToken ct,ScoutPause? pause=null) {
        var row = new JsonObject { ["path"] = Path.GetFullPath(path), ["full_dialogue"] = true, ["candidates"] = new JsonArray() };
        if (cached != null && cached.Bool("full_dialogue") && cached["dialogue"] is JsonObject && (CompilationEvidence.SourceUnchanged(cached)||RunRecovery.SourceUnchanged(cached)) &&
            (cached["transcription"]==null||speech!=null&&cached["transcription"].Text("configuration")==speech.ConfigurationKey)) {
            foreach (var field in EvidenceFields) row[field] = cached[field]?.DeepClone();
            row["source_mtime_ns"]??=cached["primary_fingerprint"]?["mtime_ns"]?.DeepClone();
            progress.Report(cached["transcription"]==null?"Reusing unchanged full-dialogue extraction.":"Reusing unchanged machine transcription · "+cached["transcription"].Text("name"));
            return row;
        }
        long size = new FileInfo(path).Length, mtime = PreviewPlanner.MtimeNs(path);
        var sample = PreviewPlanner.SampledDigest(path);
        var probe = JsonNode.Parse(await RunTool(settings.Ffprobe, ["-v", "error", "-show_streams", "-show_format", "-of", "json", path], ct))!;
        row["duration"] = double.Parse(probe["format"].Text("duration", "0"), CultureInfo.InvariantCulture);
        var streams = probe.Array("streams").Where(n => n.Text("codec_type") == "subtitle" && new[] { "subrip", "ass", "ssa", "mov_text", "webvtt", "text" }.Contains(n.Text("codec_name"))).ToList();
        var track = streams.FirstOrDefault(n => n?["tags"].Text("language") is "eng" or "en") ?? streams.FirstOrDefault();
        var cues = track == null ? new List<Cue>() : ParseSrt(await RunTool(settings.Ffmpeg, ["-nostdin", "-v", "error", "-i", path, "-map", "0:" + track.Int("index"), "-f", "srt", "-"], ct));
        var reason=track==null?"missing_subtitles":"empty_subtitles";
        if(cues.Count==0&&speech!=null) {
            var audio=SelectSpeechAudio(probe);
            if(audio==null)reason="missing_audio";
            else {
                progress.Report($"No usable subtitles: {Path.GetFileName(path)} · using local speech recognition on audio track {audio.Int("index")}.");
                try {
                    var transcript=await speech.TranscribeAsync(new(path,row["duration"]!.GetValue<double>(),audio.Int("index"),settings.Ffmpeg,Path.Combine(settings.Output,"speech-cache"),CollectionResolver.Hash(JsonSerializer.Serialize(new{size,mtime,sample}))),progress,ct,pause);
                    cues=transcript.Cues.ToList();row["transcription"]=transcript.Provenance;reason="no_speech";
                }catch(SpeechNotReadyException ex){reason="speech_not_ready";progress.Report(ex.Message);}
            }
        }
        if (new FileInfo(path).Length != size || PreviewPlanner.MtimeNs(path) != mtime || PreviewPlanner.SampledDigest(path) != sample)
            throw new IOException("Media changed while reading dialogue.");
        row["size"] = size; row["source_mtime_ns"] = mtime; row["sampled_digest"] = sample;
        if (cues.Count == 0) {
            DialogueAvailability.Mark(row, reason);
            return row;
        }
        row["dialogue"] = new JsonObject { ["cues"] = JsonSerializer.SerializeToNode(cues, Data.Options) };
        return row;
    }
    public static JsonNode? SelectSpeechAudio(JsonNode probe) {
        var audio=probe.Array("streams").Where(n=>n.Text("codec_type")=="audio").ToList();
        bool Commentary(JsonNode? n)=>n?["disposition"].Int("comment")==1||n?["tags"].Text("title").Contains("commentary",StringComparison.OrdinalIgnoreCase)==true;
        return audio.OrderBy(n=>n?["tags"].Text("language") is "eng" or "en"?0:1).ThenBy(n=>Commentary(n)?1:0)
            .ThenBy(n=>n?["disposition"].Int("default")==1?0:1).FirstOrDefault();
    }
}
