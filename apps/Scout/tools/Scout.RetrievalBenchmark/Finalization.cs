using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;

// Offline collection reconciliation only. No model calls or media access.
static class Finalization {
    public static void Run(string directory) {
        var root=Path.GetFullPath(directory);var logical=Path.Combine(root,"data");ScoutStorage.Register(logical,new ScoutDatabase(Path.Combine(root,"benchmark.db")));
        JsonObject Read(string name)=>Data.Read(Path.Combine(logical,name));void Save(string name,object data)=>Data.Save(Path.Combine(logical,name),data);
        var summary=Read("summary.json");if(!summary.Bool("complete"))throw new InvalidOperationException("Finalization requires a complete benchmark; no inference is started.");
        var fixture=Read("fixture.json");var inputs=fixture.Array("cases");var results=Read("results.json").Array("cases");
        var proofRows=new List<JsonObject>();
        foreach(var provenance in fixture.Array("provenance")) {
            var text=File.ReadAllText(provenance.Text("path"));if(CollectionResolver.Hash(text)!=provenance.Text("sha256"))throw new InvalidDataException("Source fixture changed; no proof chosen.");
            var source=JsonNode.Parse(text)!;proofRows.AddRange(source["saved_report"].Array("files").OfType<JsonObject>());
        }
        var report=new JsonObject{["files"]=new JsonArray(),["manual_corrections"]=false};
        foreach(var record in results) {
            var input=inputs.Single(i=>i.Text("case_id")==record.Text("case_id"))!;var catalog=input["catalog"]!.Deserialize<List<Episode>>(Data.Options)!;
            var proof=proofRows.SingleOrDefault(r=>r.Text("path")==input.Text("source"));
            var row=new JsonObject{["path"]=input["source"]!.DeepClone(),["matching_method"]="reasoning",["full_dialogue"]=true,
                ["duration"]=proof?["duration"]?.DeepClone(),["primary_fingerprint"]=proof?["primary_fingerprint"]?.DeepClone(),["dialogue"]=new JsonObject{["cues"]=input["cues"]!.DeepClone()},["candidates"]=new JsonArray()};
            if(record?["result"] is JsonObject result) {
                row["reasoning"]=result.DeepClone();foreach(var match in result.Array("matches"))row.Array("candidates").Add(new JsonObject{["episode"]=JsonSerializer.SerializeToNode(catalog.Single(e=>e.Id==match.Int("episode_id")),Data.Options)});
            }else row["error"]=record.Text("error");
            report.Array("files").Add(row);
        }
        var groups=CollectionResolver.Resolve(report);Save("collection-report.json",report);
        var collection=new{groups=groups.Count,acceptedGroups=groups.Count(g=>g.Episode!=null),heldGroups=groups.Count(g=>g.Episode==null),conflicts=groups.Count(g=>g.Status=="unresolved_conflict"),duplicateCopies=groups.Sum(g=>g.Copies-1),
            decisions=groups.Select(g=>new{g.Id,g.Status,g.Code,g.Title,files=g.Rows.Select(r=>r.Text("path")),episodeIds=g.Episodes.Select(e=>e.Id)}),manualCorrections=false};
        Save("collection-summary.json",collection);summary["collection"]=JsonSerializer.SerializeToNode(collection,Data.Options);Save("summary.json",summary);
        string E(string value)=>WebUtility.HtmlEncode(value);
        var html=new StringBuilder("<!doctype html><html lang=en><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><title>Scout compact evidence results</title><style>body{font:16px/1.5 Segoe UI;max-width:1200px;margin:30px auto;padding:20px;background:#111b28;color:#e4edfb}h1{color:#91c3ff}td,th{padding:12px;border-bottom:1px solid #405269;text-align:left;vertical-align:top;overflow-wrap:anywhere}table{width:100%;border-collapse:collapse}pre{white-space:pre-wrap;overflow-wrap:anywhere}details{margin:20px 0}</style><h1>Compact evidence matching</h1><p>CPU only · thinking off · saved original dialogue · no manual corrections · Scout results and videos unchanged.</p>");
        if(summary["default_workflow_validation"] is JsonObject defaultProof)
            html.Append($"<p>The measured test included {defaultProof.Int("omitted_excerpt_audits")} excerpt audits that all required a full check. Scout's default now skips those calls. All {defaultProof.Array("cases").Count} outcomes were validated using exact remaining requests and unchanged saved model responses. The displayed times include the experiment; the simpler default was not timed in a second inference run.</p>");
        html.Append($"<p>{collection.acceptedGroups} assigned recording groups · {collection.heldGroups} held groups · {collection.conflicts} conflicts · {collection.duplicateCopies} duplicate copies grouped · {summary["seconds"]!.GetValue<double>()/60:0.0} minutes of matching.</p><table><tr><th>Recording</th><th>Final assignment</th><th>Time</th><th>Requests</th></tr>");
        foreach(var group in groups) {
            var records=results.Where(r=>group.Rows.Any(f=>f.Text("path")==r.Text("source"))).ToArray();
            string decision=group.Episode!=null?group.Code+" · "+group.Title:"Held — "+group.Status;
            if(group.Episode==null) {
                var proposals=records.SelectMany(r=>r.Array("unaccepted_model_proposals")).Select(p=>p.Text("code")+" · "+p.Text("title")).Distinct().ToArray();
                if(proposals.Length>0)decision+=". Unaccepted model proposal: "+string.Join(" / ",proposals);
            }
            html.Append("<tr><td>").Append(string.Join("<br>",group.Rows.Select(r=>E(Path.GetFileName(r.Text("path")))))).Append("</td><td>").Append(E(decision)).Append("</td><td>").Append((records.Sum(r=>r!["seconds"]!.GetValue<double>())/60).ToString("0.0")).Append(" min</td><td>").Append(records.Sum(r=>r.Array("requests").Count)).Append("</td></tr>");
        }
        html.Append("</table><p>References are inherited labels or previous model suggestions, not independent ground truth. The native run used fresh inference except for identical dialogue reuse. Candidate retrieval is provisional; the default verification examines complete dialogue. Multipart/conflict review retains two complete-dialogue audits. Each saved result states its actual audit scope. Earlier Farpoint timing covered audits only; this run also searches for candidates.</p>");
        if(summary.Bool("revalidated_saved_responses"))html.Append("<p>Source-context validation used exact saved requests and unchanged responses, with no new inference. Timings remain those of the original measured run.</p>");
        html.Append("<details><summary>Measurements and limitations</summary><pre>").Append(E(summary.ToJsonString(Data.Options))).Append("</pre></details>");
        foreach(var row in results)html.Append("<details><summary>").Append(E(Path.GetFileName(row.Text("source")))).Append(" · original decisions and evidence</summary><pre>").Append(E(row!.ToJsonString(Data.Options))).Append("</pre></details>");
        foreach(var episode in inputs.SelectMany(i=>i!["catalog"]!.Deserialize<List<Episode>>(Data.Options)!).Where(e=>e.DescriptionSource!=null).DistinctBy(e=>e.Id)) {
            var s=episode.DescriptionSource!;html.Append($"<p>{E(episode.Code)} description from <a href='{E(s.Url)}'>{E(s.Provider)}</a>; selected-provider identity retained. TVmaze data: <a href='https://creativecommons.org/licenses/by-sa/4.0/'>CC BY-SA 4.0</a>.</p>");
        }
        File.WriteAllText(Path.Combine(root,"results.html"),html.Append("</html>").ToString());Console.WriteLine(JsonSerializer.Serialize(collection,Data.Options));
    }
}
