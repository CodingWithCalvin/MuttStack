using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Scout.Core;

static class SubtitleReport {
 public static void Write(string directory) {
  var root=Path.GetFullPath(directory);
  using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(root,"benchmark.db"),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());db.Open();
  JsonObject Read(string name) {
   using var cmd=db.CreateCommand();cmd.CommandText="SELECT content FROM documents WHERE path=$path COLLATE NOCASE";
   cmd.Parameters.AddWithValue("$path",Path.Combine(root,"data",name));
   return JsonNode.Parse((byte[])(cmd.ExecuteScalar()??throw new InvalidDataException("Missing report document: "+name)))!.AsObject();
  }
  var summary=Read("summary.json");var rows=Read("results.json").Array("cases");var source=Read("downloads.json");
  bool blocked=summary.Int("reference_files")==0;string E(string text)=>WebUtility.HtmlEncode(text);
  var html=new StringBuilder("<!doctype html><html lang=en><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><title>Scout subtitle comparison</title><style>body{font:16px/1.5 Segoe UI;max-width:1200px;margin:30px auto;padding:20px;background:#111b28;color:#e4edfb}a{color:#91c3ff}td,th{padding:12px;text-align:left;border-bottom:1px solid #405269}pre{white-space:pre-wrap;overflow-wrap:anywhere}</style><h1>Subtitle dialogue comparison</h1><p>Saved dialogue · no LLM · isolated results · no video or Scout changes</p>");
  if(blocked) {
   html.Append($"<h2>Reference access blocked</h2><p>No reference subtitles could be downloaded. The {rows.Count} saved cases were not evaluated against external dialogue. This is an access failure, not a failed matching result or an accuracy measurement.</p>");
  }else {
   html.Append($"<p>{summary.Int("substantial_overlap")} of {summary.Int("cases")} files show substantial text overlap · {summary.Int("remaining")} without a decisive single-reference match · {summary.Int("reference_files")} reference files · {summary["total_seconds"]!.GetValue<double>():0.0} seconds including retrieval ({summary["matching_seconds"]!.GetValue<double>():0.00} seconds comparing text).</p><p>These are subtitle correspondences, not verified TVDB assignments. Upload numbering can differ from the selected metadata provider. Percentages below measure shared five-word sequences, not model confidence. A recording spanning multiple reference files may remain unresolved.</p>");
   html.Append("<table><tr><th>Recording</th><th>Strongest reference</th><th>Source overlap</th><th>Reference overlap</th><th>Result</th></tr>");
   foreach(var row in rows) {
    var best=row!.Array("ranking").FirstOrDefault();var score=best?["overlap"];
    string verdict=best==null?"Not evaluated — no reference":row.Bool("substantial_text_overlap")?"Substantial textual overlap":"No decisive text match";
    string Coverage(string key)=>score?[key] is JsonNode value?value.GetValue<double>().ToString("P1"):"Not evaluated";
    html.Append($"<tr><td>{E(Path.GetFileName(row.Text("source")))}</td><td>{E(best.Text("name","No reference available"))}</td><td>{Coverage("source_coverage")}</td><td>{Coverage("reference_coverage")}</td><td>{E(verdict)}</td></tr>");
   }
   html.Append("</table><details><summary>Measurements</summary><pre>").Append(E(summary.ToJsonString(Data.Options))).Append("</pre></details>");
   foreach(var row in rows)html.Append("<details><summary>").Append(E(row.Text("case_id"))).Append(" · candidate rankings</summary><pre>").Append(E(row!.ToJsonString(Data.Options))).Append("</pre></details>");
  }
  html.Append("<p>").Append(E(summary.Text("limitations"))).Append("</p>");
  foreach(var failure in source.Array("failures"))html.Append("<p><a href=\"").Append(E(failure.Text("page"))).Append("\">Reference page</a>: ").Append(E(failure.Text("error"))).Append("</p>");
  foreach(var download in source.Array("downloads"))html.Append("<p>Reference source: <a href=\"").Append(E(download.Text("page"))).Append("\">").Append(E(download.Text("series"))).Append("</a></p>");
  File.WriteAllText(Path.Combine(root,"results.html"),html.Append("</html>").ToString());
 }
}
