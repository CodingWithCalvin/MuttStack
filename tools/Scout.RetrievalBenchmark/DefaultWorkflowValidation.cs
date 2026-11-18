using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Scout.Core;

// Validate removal of an unsuccessful excerpt-audit experiment against the exact
// remaining requests and untouched native responses. This is not new inference
// and does not estimate a new end-to-end runtime or relabel any prediction.
static class DefaultWorkflowValidation {
    public static async Task Run(string directory) {
        var root=Path.GetFullPath(directory);var logical=Path.Combine(root,"data");
        var docs=new Dictionary<string,JsonObject>(StringComparer.OrdinalIgnoreCase);
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(root,"benchmark.db"),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString())) {
            db.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT path,content FROM documents";using var reader=cmd.ExecuteReader();
            while(reader.Read())docs[Path.GetRelativePath(logical,reader.GetString(0))]=JsonNode.Parse((byte[])reader[1])!.AsObject();
        }
        var summary=docs["summary.json"];if(!summary.Bool("complete"))throw new InvalidOperationException("The native benchmark must finish first.");
        var originals=docs["results.json"].Array("cases");
        if(originals.Any(r=>r.Text("state")=="accepted"&&r?["result"]?["grounded_verification"].Bool("full_dialogue")!=true))
            throw new InvalidOperationException("An acceptance used excerpts only; removing its audit requires a fresh model test.");
        var fixture=docs["fixture.json"];var settings=fixture["settings"]!.Deserialize<AppSettings>(Data.Options)!;
        settings.MaxRequestsPerRun=200;if(settings.Inference().AllowExcerptVerification)throw new InvalidOperationException("The production default still enables excerpt audits.");
        var memo=new Dictionary<string,JsonObject>();var checks=new JsonArray();int kept=0,skipped=0;
        foreach(var input in fixture.Array("cases")) {
            string id=input.Text("case_id");var original=originals.Single(r=>r.Text("case_id")==id)!;
            var all=docs.Where(p=>p.Key.StartsWith(Path.Combine("requests",id)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&p.Key.EndsWith("-request.json",StringComparison.Ordinal))
                .OrderBy(p=>int.Parse(Path.GetFileName(p.Key).Split('-')[0])).ToArray();
            bool Excerpt(JsonObject request)=>request["response_format"]?["json_schema"]?["schema"]?["properties"]?["full_dialogue_required"]!=null;
            var remaining=all.Where(p=>!Excerpt(p.Value)).Select(p=>(Request:p.Value,Response:docs[p.Key.Replace("-request.json","-response.json",StringComparison.Ordinal)])).ToArray();
            using var handler=new ExactResponses(remaining);using var http=new HttpClient(handler);
            var cues=input!["cues"]!.Deserialize<List<Cue>>(Data.Options)!;var catalog=input["catalog"]!.Deserialize<List<Episode>>(Data.Options)!;
            string key=input.Text("dialogue_hash")+":"+CollectionResolver.Hash(input["catalog"]!.ToJsonString());
            JsonObject? result=null;Exception? error=null;
            try {
                if(memo.TryGetValue(key,out var prior))result=prior.DeepClone().AsObject();
                else {result=await new DialogueMatcher(http,settings.Inference()).IdentifyAsync(cues,catalog,settings.Model,"http://127.0.0.1:1/v1/chat/completions");memo[key]=result.DeepClone().AsObject();}
            }catch(ExactRequestException){throw;}
            catch(Exception ex){error=ex;}
            if(handler.Used!=remaining.Length)throw new ExactRequestException(id+": not every expected request was consumed.");
            if(original.Text("error")!="") {
                if(error?.Message!=original.Text("error")||error?.GetType().Name!=original.Text("error_type"))throw new ExactRequestException(id+": validation outcome changed.");
            }else if(error!=null||result==null||!JsonNode.DeepEquals(Normalized(original["result"]!),Normalized(result)))
                throw new ExactRequestException(id+": an assignment, evidence, or coverage decision changed.");
            kept+=handler.Used;skipped+=all.Length-remaining.Length;
            checks.Add(new JsonObject{["case_id"]=id,["outcome_unchanged"]=true,["exact_requests"]=handler.Used,["omitted_excerpt_audits"]=all.Length-remaining.Length});
            Console.WriteLine(id+": exact remaining requests and unchanged outcome verified.");
        }
        var proof=new {complete=true,revision=DialogueMatcher.RequestRevision,cases=checks,exactRequests=kept,omittedExcerptAudits=skipped,
            omittedCallsMeasuredSeconds=originals.SelectMany(r=>r.Array("requests")).Where(r=>r.Text("stage")=="evidence_audit").Sum(r=>r!["seconds"]!.GetValue<double>()),
            newInferenceRequests=0,modelResponsesEdited=false,appDatabaseWrites=false,mediaAccess=false,
            timingScope="The native benchmark includes these discarded excerpt calls. Their recorded duration is not a new measurement of the default workflow. Every remaining prompt, schema and request parameter matches its original request; the saved native response is returned unchanged."};
        ScoutStorage.Register(logical,new ScoutDatabase(Path.Combine(root,"benchmark.db")));
        Data.Save(Path.Combine(logical,"default-workflow-validation.json"),proof);
        summary["default_workflow_validation"]=JsonSerializer.SerializeToNode(proof,Data.Options);Data.Save(Path.Combine(logical,"summary.json"),summary);
        Console.WriteLine($"Validated {checks.Count} cases, {kept} exact requests, {skipped} redundant excerpt audits omitted; no new inference.");
    }
    static JsonNode Normalized(JsonNode source) {
        var copy=source.DeepClone();Visit(copy);return copy;
        static void Visit(JsonNode node) {
            if(node is JsonObject obj) {
                // Only routing diagnostics differ. Do not remove assignments,
                // verdicts, source citations, coverage, or raw complete audits.
                obj.Remove("request_revision");obj.Remove("fallback_reason");
                if(obj["raw_audits"] is JsonArray audits)obj["raw_audits"]=new JsonArray(audits.Where(a=>a.Text("scope")=="complete_dialogue").Select(a=>a!.DeepClone()).ToArray());
                foreach(var item in obj.ToArray())if(item.Value!=null)Visit(item.Value);
            }else if(node is JsonArray array)foreach(var item in array)if(item!=null)Visit(item);
        }
    }
    sealed class ExactRequestException(string message):InvalidOperationException(message);
    sealed class ExactResponses((JsonObject Request,JsonObject Response)[] records):HttpMessageHandler {
        public int Used {get;private set;}
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
            var body=JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct));
            if(Used>=records.Length||!JsonNode.DeepEquals(body,records[Used].Request))throw new ExactRequestException("New or different model input requested; no response was fabricated.");
            var response=records[Used++].Response;return new((HttpStatusCode)response.Int("http_status")){Content=new StringContent(response.Text("body"))};
        }
    }
}
