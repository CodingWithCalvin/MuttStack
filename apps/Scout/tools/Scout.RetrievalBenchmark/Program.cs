using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;

if(args.Length==3&&args[0]=="--tune-threads"){await ThreadBenchmark.Run(args[1],args[2]);return;}
if(args.Length==3&&args[0]=="--revalidate"){await Revalidation.Run(args[1],args[2]);return;}
if(args.Length==4&&args[0]=="--prepare"){Preparation.Run(args[1],args[2],args[3]);return;}
if(args.Length==2&&args[0]=="--finalize"){Finalization.Run(args[1]);return;}
if(args.Length==2&&args[0]=="--validate-default"){await DefaultWorkflowValidation.Run(args[1]);return;}
if(args.Length==3&&args[0]=="--check-idle"){using var probe=new ScoutIdleGate(args[1],args[2]);Console.WriteLine((await probe.Check(default)).Detail);return;}
if(args.Length!=2)throw new ArgumentException("Scout.RetrievalBenchmark <isolated fixture.json> <empty output directory>");
var inputText=File.ReadAllText(args[0]);var fixture=JsonNode.Parse(inputText)!;
var output=Path.GetFullPath(args[1]);if(Directory.Exists(output)&&Directory.EnumerateFileSystemEntries(output).Any())throw new InvalidOperationException("Use an empty output directory. No completed inference is rerun.");
Directory.CreateDirectory(output);var logical=Path.Combine(output,"data");ScoutStorage.Register(logical,new ScoutDatabase(Path.Combine(output,"benchmark.db")));
void Save(string name,object data)=>Data.Save(Path.Combine(logical,name),data);
var settings=fixture["settings"]!.Deserialize<AppSettings>(Data.Options)!;
if(settings.Provider!="managed"||settings.GpuLayers!=0||settings.ThinkingEnabled!=false||settings.ManagedThinking)throw new InvalidOperationException("CPU-only and thinking off are required.");
settings.AllowDownloads=false;settings.AutoDownloadMissing=false;settings.MaxRequestsPerRun=200;
var inputs=fixture.Array("cases");var results=new JsonArray();var wall=Stopwatch.StartNew();string active="",phase="Preparing",failure="";var gate=new object();
void Status(string state,string message){lock(gate){phase=message;Save("status.json",new{state,message,active,updated=DateTimeOffset.UtcNow,seconds=wall.Elapsed.TotalSeconds});}Console.WriteLine(DateTime.Now.ToString("HH:mm:ss")+" "+message);}
using var stop=new CancellationTokenSource();using var stopMonitor=new CancellationTokenSource();Task monitor=Task.CompletedTask;LocalRuntime? runtime=null;DialogueMatcher? matcher=null;
using var idle=new ScoutIdleGate(fixture.Text("idle_status_database"),new Uri(settings.LocalEndpoint).GetLeftPart(UriPartial.Authority));
using var stopHeartbeat=new CancellationTokenSource();
async Task Heartbeat(){using var timer=new PeriodicTimer(TimeSpan.FromSeconds(30));try{while(await timer.WaitForNextTickAsync(stopHeartbeat.Token))lock(gate)Save("heartbeat.json",new{phase,active,updated=DateTimeOffset.UtcNow});}catch(OperationCanceledException){}}
var heartbeat=Heartbeat();Save("fixture.json",fixture);
try {
    await idle.Wait(m=>Status("waiting",m),stop.Token);stop.CancelAfter(TimeSpan.FromHours(10));
    var profile=ModelDownloads.Profiles.Single(p=>p.Id==settings.DownloadProfile);Status("verifying","Verifying installed model weights.");
    await using(var file=File.OpenRead(settings.ModelFile))if(file.Length!=profile.Bytes||Convert.ToHexStringLower(await SHA256.HashDataAsync(file,stop.Token))!=profile.Sha256)throw new IOException("Model checksum mismatch.");
    using(var socket=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp)){socket.Bind(new IPEndPoint(IPAddress.Loopback,0));settings.LocalEndpoint=$"http://127.0.0.1:{((IPEndPoint)socket.LocalEndPoint!).Port}/v1/chat/completions";}
    Save("manifest.json",new{inputHash=CollectionResolver.Hash(inputText),coreSha256=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(DialogueMatcher).Assembly.Location))),revision=DialogueMatcher.RequestRevision,
        model=settings.Model,modelSha256=profile.Sha256,runtime=RuntimeDownloads.Profile(settings).Id,endpoint=settings.LocalEndpoint,cpuThreads=settings.CpuThreads,promptThreads=settings.EffectivePromptThreads,gpuLayers=0,thinking=false,
        fullSwaCache=settings.ManagedFullSwaCache,context=settings.EffectiveContextSize,outputTokens=settings.EffectiveOutputTokens,timeoutSeconds=settings.EffectiveTimeoutSeconds,
        mode=fixture.Bool("try_excerpt_verification")?"experimental-excerpt-audits-with-full-fallback":"retrieval-plus-full-dialogue-audits",tryExcerptVerification=fixture.Bool("try_excerpt_verification"),manualCorrections=false,mediaAccess=false,appDatabaseWrites=false,appDatabaseReadScope="workspace status only",referenceLabelsSentToModel=false});
    await idle.Wait(m=>Status("waiting",m),stop.Token);
    async Task Monitor(){using var timer=new PeriodicTimer(TimeSpan.FromSeconds(15));try{while(await timer.WaitForNextTickAsync(stopMonitor.Token)){if(File.Exists(Path.Combine(output,"stop.request"))){failure="Isolated benchmark stop requested; completed checkpoints retained.";stop.Cancel();return;}var state=await idle.Check(stopMonitor.Token);if(!state.Idle){failure="User Scout became active: "+state.Detail;stop.Cancel();return;}}}catch(OperationCanceledException) when(stopMonitor.IsCancellationRequested){}catch(Exception ex){failure="Idle monitoring failed: "+ex.Message;stop.Cancel();}}
    monitor=Monitor();runtime=await LocalRuntime.StartAsync(settings,new ProgressCallback(m=>Status("loading",m)),stop.Token);
    using var recorder=new Recorder(Save,m=>Status("inference",m));using var http=new HttpClient(recorder){Timeout=TimeSpan.FromSeconds(settings.EffectiveTimeoutSeconds)};
    matcher=new DialogueMatcher(http,settings.Inference(allowExcerptVerification:fixture.Bool("try_excerpt_verification")));var exact=new Dictionary<string,JsonObject>();
    foreach(var input in inputs) {
        active=input.Text("case_id");var cues=input!["cues"]!.Deserialize<List<Cue>>(Data.Options)!;var catalog=input["catalog"]!.Deserialize<List<Episode>>(Data.Options)!;
        string hash=CollectionResolver.Hash(JsonSerializer.Serialize(cues,Data.Options));if(hash!=input.Text("dialogue_hash"))throw new InvalidDataException("Fixture dialogue hash mismatch.");
        string reuse=hash+":"+CollectionResolver.Hash(input["catalog"]!.ToJsonString());recorder.Start(active);var timer=Stopwatch.StartNew();
        var row=new JsonObject{["case_id"]=active,["source"]=input["source"]!.DeepClone(),["series"]=input["series"]!.DeepClone(),["dialogue_hash"]=hash,["cue_count"]=cues.Count};
        Status("inference",$"{results.Count+1}/{inputs.Count}: {active} — fresh production matching.");
        try {
            if(exact.TryGetValue(reuse,out var prior)){row["result"]=prior.DeepClone();row["reused_identical_dialogue"]=true;}
            else{var result=await matcher.IdentifyAsync(cues,catalog,settings.Model,settings.Endpoint,stop.Token);row["result"]=result;exact[reuse]=result.DeepClone().AsObject();}
            row["state"]=row["result"].Text("assessment")=="supported"?"accepted":"held";
        }catch(OperationCanceledException) when(stop.IsCancellationRequested){throw;}
        catch(Exception ex){row["state"]="held";row["error"]=ex.Message;row["error_type"]=ex.GetType().Name;if(ex.Data["model_attempts"] is string attempts)row["model_attempts"]=JsonNode.Parse(attempts);if(ex.Data["model_candidate_ids"] is string candidateIds)row["model_candidate_ids"]=JsonNode.Parse(candidateIds);}
        row["seconds"]=timer.Elapsed.TotalSeconds;row["requests"]=new JsonArray(recorder.Records.Select(r=>r.DeepClone()).ToArray());
        // References are read only AFTER inference and never passed to the matcher.
        if(input["reference_ids"] is JsonArray reference) {
            var actual=row["result"].Array("matches").Select(m=>m.Int("episode_id")).Order().ToArray();var expected=reference.Select(n=>n!.GetValue<int>()).Order().ToArray();
            row["reference_ids"]=reference.DeepClone();row["reference_kind"]=input["reference_kind"]?.DeepClone();
            row["reference_agreement"]=row.Text("error")==""&&actual.SequenceEqual(expected)&&(expected.Length>0||row["result"].Text("assessment")==input.Text("reference_kind"));
            row["accepted_reference_disagreement"]=actual.Length>0&&!actual.SequenceEqual(expected);
            row["held_non_episode_reference"]=actual.Length==0&&input.Text("reference_kind")=="non_episode";
        }
        Save($"cases/{active}.json",row);results.Add(row);Save("results.json",new{cases=results});Render();
        Status("processing",$"{active}: {row.Text("state")} in {timer.Elapsed.TotalMinutes:0.0} minutes."+(row.Text("error")==""?"":" "+row.Text("error")));
    }
}catch(Exception ex){if(failure=="")failure=ex.Message;Status("failed",failure);}
finally {
    stopMonitor.Cancel();await monitor;stopHeartbeat.Cancel();await heartbeat;if(runtime!=null)await runtime.DisposeAsync();
    var fresh=results.Where(r=>!r.Bool("reused_identical_dialogue")).ToList();var requests=fresh.SelectMany(r=>r.Array("requests")).ToList();
    var summary=new{complete=failure==""&&results.Count==inputs.Count,failure,cases=results.Count,uniqueDialogues=fresh.Count,reused=results.Count-fresh.Count,
        accepted=results.Count(r=>r.Text("state")=="accepted"),held=results.Count(r=>r.Text("state")=="held"),errors=results.Count(r=>r.Text("error")!=""),
        referenceAgreement=results.Count(r=>r.Bool("reference_agreement")),acceptedReferenceDisagreement=results.Count(r=>r.Bool("accepted_reference_disagreement")),heldNonEpisodeReference=results.Count(r=>r.Bool("held_non_episode_reference")),
        seconds=fresh.Sum(r=>r!["seconds"]!.GetValue<double>()),wallSeconds=wall.Elapsed.TotalSeconds,usage=matcher?.Usage,freshRequests=requests.Count,
        promptSeconds=requests.Sum(r=>(r?["timings"]?["prompt_ms"]?.GetValue<double>()??0)/1000),generationSeconds=requests.Sum(r=>(r?["timings"]?["predicted_ms"]?.GetValue<double>()??0)/1000),
        promptTokensEvaluated=requests.Sum(r=>r?["timings"]?["prompt_n"]?.GetValue<long>()??0),cachedTokens=requests.Sum(r=>r?["timings"]?["cache_n"]?.GetValue<long>()??0),
        evidenceOnlyAccepted=results.Count(r=>r.Text("state")=="accepted"&&r?["result"]?["grounded_verification"] is JsonObject g&&g.ContainsKey("full_dialogue")&&!g.Bool("full_dialogue")),
        fullDialogueAccepted=results.Count(r=>r.Text("state")=="accepted"&&r?["result"]?["grounded_verification"].Bool("full_dialogue")==true),
        manualCorrections=false,mediaAccess=false,appDatabaseWrites=false,limitations="Inherited references and prior model suggestions are not independent ground truth. Fourteen cases include one identical dialogue reused automatically. Earlier timings use older matching revisions; compare matching stages and report changes in acceptance, not just speed."};
    Save("summary.json",summary);Render();Status(summary.complete?"complete":"failed",summary.complete?$"Complete: {summary.accepted} accepted, {summary.held} held, {summary.errors} errors.":"Benchmark blocked: "+failure);if(!summary.complete)Environment.ExitCode=1;
}
void Render() {
    string E(string s)=>WebUtility.HtmlEncode(s);
    var html=new StringBuilder("<!doctype html><html lang=en><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><title>Scout compact evidence results</title><style>body{font:16px/1.5 Segoe UI;max-width:1200px;margin:30px auto;padding:20px;background:#111b28;color:#e4edfb}h1{color:#91c3ff}td,th{padding:12px;border-bottom:1px solid #405269;text-align:left;vertical-align:top}table{width:100%;border-collapse:collapse}pre{white-space:pre-wrap;overflow-wrap:anywhere}details{margin:20px 0}</style><h1>Compact evidence matching</h1><p>CPU only · thinking off · original saved dialogue · fully automated · Scout results and video files untouched.</p><table><tr><th>Recording</th><th>Final result</th><th>Time</th><th>Requests</th></tr>");
    foreach(var row in results.OfType<JsonObject>()) {
        var input=inputs.Single(c=>c.Text("case_id")==row.Text("case_id"))!;var catalog=input["catalog"]!.Deserialize<List<Episode>>(Data.Options)!;
        string verdict=row.Text("state")=="accepted"?string.Join(" + ",row["result"].Array("matches").Select(m=>catalog.Single(e=>e.Id==m.Int("episode_id"))).Select(e=>e.Code+" · "+e.Title)):"Held — "+row.Text("error",row["result"].Text("summary"));
        html.Append($"<tr><td>{E(Path.GetFileName(row.Text("source")))}</td><td>{E(verdict)}</td><td>{row!["seconds"]!.GetValue<double>()/60:0.0} min{(row.Bool("reused_identical_dialogue")?" (identical dialogue reused)":"")}</td><td>{row.Array("requests").Count}</td></tr>");
    }
    html.Append("</table><p>This development regression suite uses inherited references, not independently verified truth. Candidate retrieval uses selected original passages; omissions remain a recall risk. Default verification examines complete dialogue. Excerpt-audit experiments require an explicit fixture flag. Combined assignments retain two full-dialogue audits. No predictions were manually changed.</p>");
    if(ScoutStorage.Exists(Path.Combine(logical,"summary.json")))html.Append("<details><summary>Benchmark measurements and limitations</summary><pre>").Append(E(Data.Read(Path.Combine(logical,"summary.json")).ToJsonString(Data.Options))).Append("</pre></details>");
    foreach(var row in results)html.Append("<details><summary>").Append(E(row.Text("case_id")+" · original decisions and evidence")).Append("</summary><pre>").Append(E(row!.ToJsonString(Data.Options))).Append("</pre></details>");
    foreach(var input in inputs)foreach(var episode in input!["catalog"]!.Deserialize<List<Episode>>(Data.Options)!.Where(e=>e.DescriptionSource!=null)) {
        var s=episode.DescriptionSource!;html.Append($"<p>{E(episode.Code)} description supplemented from <a href='{E(s.Url)}'>{E(s.Provider)}</a>; selected-provider identity retained. TVmaze data: <a href='https://creativecommons.org/licenses/by-sa/4.0/'>CC BY-SA 4.0</a>.</p>");
    }
    File.WriteAllText(Path.Combine(output,"results.html"),html.Append("</html>").ToString());
}
sealed class ProgressCallback(Action<string> callback):IProgress<string>{public void Report(string value)=>callback(value);}
sealed class Recorder(Action<string,object> save,Action<string> progress):DelegatingHandler(new HttpClientHandler{AllowAutoRedirect=false}) {
    string active="";public List<JsonObject> Records=[];public void Start(string id){active=id;Records.Clear();}
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
        var body=JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;string key=$"requests/{active}/{Records.Count+1:00}";save(key+"-request.json",body);
        var schema=body["response_format"]!["json_schema"]!["schema"]!;string stage=schema["properties"]?["search_more"]!=null?"candidate_search":schema["properties"]?["full_dialogue_required"]!=null?"evidence_audit":"full_dialogue_audit";
        var record=new JsonObject{["stage"]=stage,["index"]=Records.Count+1,["state"]="running",["started"]=DateTimeOffset.UtcNow,["thinking"]=body["chat_template_kwargs"].Bool("enable_thinking")};
        Records.Add(record);save(key+"-status.json",record);progress($"{active}: {stage}, request {Records.Count}.");var watch=Stopwatch.StartNew();
        try {
            var response=await base.SendAsync(request,ct);var raw=await response.Content.ReadAsStringAsync(ct);save(key+"-response.json",new{httpStatus=(int)response.StatusCode,body=raw});var data=JsonNode.Parse(raw)!;
            record["state"]="complete";record["seconds"]=watch.Elapsed.TotalSeconds;record["usage"]=data["usage"]?.DeepClone();record["timings"]=data["timings"]?.DeepClone();save(key+"-status.json",record);progress($"{active}: {stage} returned in {watch.Elapsed.TotalMinutes:0.0} minutes.");return response;
        }catch(Exception ex){record["state"]="failed";record["seconds"]=watch.Elapsed.TotalSeconds;record["error"]=ex.Message;save(key+"-status.json",record);throw;}
    }
}
