using System.Text.Json.Nodes;
namespace Scout.Core;

public sealed partial class MediaPipeline {
    static void ReportCandidate(JsonObject row,IProgress<string> progress,bool reused=false) {
        if(row.Text("error")!=""||row.Array("candidates").Count==0)return;
        var episodes=row.Array("candidates").Select(c=>Data.Episode(c!["episode"]!)).ToList();
        var method=Subtitles.SubtitleResult.IsSubtitle(row)?"subtitles":"AI";
        if(row["transcription"]!=null)method+=" from machine transcription";
        var label=row["reasoning"].Text("coverage")=="partial"?"Possible episode part":reused?"Saved match":"Match found";
        progress.Report($"{label}: {Path.GetFileName(row.Text("path"))} → {string.Join(" / ",episodes.Select(e=>$"{e.Code} — {e.Title}"))} ({method}; collection checks pending).");
    }
    static void ReportResolvedMatches(JsonObject report,IProgress<string> progress,IReadOnlySet<string>? selected) {
        var groups=CollectionResolver.Resolve(report).Where(g=>selected==null||g.Rows.Any(r=>selected.Contains(Path.GetFullPath(r.Text("path"))))).ToList();
        foreach(var group in groups) {
            var names=string.Join(", ",group.Rows.Where(r=>selected==null||selected.Contains(Path.GetFullPath(r.Text("path")))).Select(r=>Path.GetFileName(r.Text("path"))));
            if(group.Episode!=null)progress.Report($"Matched: {names} → {group.Code} — {group.Title} (collection checks complete).");
            else if(group.Compilation==null&&group.Claims.Count>0)progress.Report($"Held for review: {names} — {EvidenceReview.Create(group).Assessment}. No episode assignment accepted.");
        }
        int identityPending=groups.Count(g=>g.Status=="unassigned"&&g.Rows.Any(r=>Subtitles.SubtitleResult.UnresolvedStatus(r["subtitle_match"] as JsonObject)=="Subtitle matched — episode identity unresolved"));
        if(identityPending>0)progress.Report($"Subtitle dialogue matched for {identityPending} additional recording group(s), but their episode identities remain unverified. They are held for reference mapping or optional AI review.");
        progress.Report($"Matching results: {groups.Count(g=>g.Episode!=null)} recording group(s) matched, {groups.Count(g=>g.Category=="Unresolved")} unresolved, {groups.Count(g=>g.NotProcessed)} not processed.");
    }
}
