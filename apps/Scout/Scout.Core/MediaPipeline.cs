using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Scout.Core.Subtitles;
namespace Scout.Core;
public sealed record MatchSettings(string Root,string Catalog,string Output,string Model,string Endpoint,string Ffmpeg="ffmpeg",string Ffprobe="ffprobe",string? DialogueInput=null,IReadOnlyList<string>? SelectedFiles=null,SeriesIdentity? SeriesIdentity=null,bool Restart=false,bool Reanalyze=false,bool ReviewIssues=false,bool NewFilesOnly=false,IdentificationScope? Scope=null,ScoutContentOptions? ContentOptions=null,bool UseAiFallback=true);
public sealed partial class MediaPipeline(DialogueMatcher matcher,Func<string,IEnumerable<string>,CancellationToken,Task<string>>? execute=null,SubtitleMatcher? subtitles=null,ISpeechTranscriber? speech=null)
{
    private Task<string> RunTool(string exe,IEnumerable<string> args,CancellationToken ct)=>(execute??ExecuteAsync)(exe,args,ct);
    public static async Task<string> ExecuteAsync(string exe,IEnumerable<string> args,CancellationToken ct) {
        var start=new ProcessStartInfo(exe) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in args)start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new IOException("Cannot start "+exe);
        using var cancel=ct.Register(()=>{try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}});
        var output=process.StandardOutput.ReadToEndAsync(ct);var error=process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);var stderr=await error;var stdout=await output;
        if(process.ExitCode!=0)throw new IOException(Path.GetFileName(exe)+" failed: "+stderr[..Math.Min(stderr.Length,600)]);
        return stdout;
    }
    public static List<Cue> ParseSrt(string value) {
        var cues=new List<Cue>();
        foreach(var block in Regex.Split(value.Replace("\r","").Trim(),@"\n\s*\n")) {
            var m=Regex.Match(block,@"(\d+):(\d+):(\d+)[,.](\d+)\s*-->[^\n]*\n(.*)",RegexOptions.Singleline);
            if(!m.Success)continue;
            double time=int.Parse(m.Groups[1].Value)*3600+int.Parse(m.Groups[2].Value)*60+int.Parse(m.Groups[3].Value)+int.Parse(m.Groups[4].Value)/1000.0;
            var text=Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(m.Groups[5].Value,@"<[^>]+>|\{[^}]*\}","")),@"\s+"," ").Trim();
            if(text.Length>0)cues.Add(new(time,text));
        }
        return cues;
    }
    public async Task<string> RunAsync(MatchSettings settings,IProgress<string> progress,CancellationToken ct,ScoutPause? pause=null) {
        pause?.Checkpoint();ct.ThrowIfCancellationRequested();
        if(!Directory.Exists(settings.Root))throw new DirectoryNotFoundException("Video folder does not exist.");
        if(PreviewPlanner.Within(settings.Output,settings.Root))throw new InvalidOperationException("Results must be outside the media library.");
        if(subtitles==null&&settings.UseAiFallback){matcher.ValidateEndpoint(settings.Endpoint);if(string.IsNullOrWhiteSpace(settings.Model))throw new ArgumentException("Choose a model ID.");}
        var episodes=Data.Catalog(settings.Catalog);
        var reportPath=Path.Combine(settings.Output,"report.json");
        var continuing=settings.Restart||settings.Reanalyze||settings.ReviewIssues||settings.NewFilesOnly;
        var previous=continuing&&ScoutStorage.Exists(reportPath)?Data.Read(reportPath):new JsonObject();
        if(previous["series_identity"] is JsonObject previousIdentity && settings.SeriesIdentity is {} selectedIdentity &&
            previousIdentity.Text("provider")+":"+previousIdentity.Text("id")!=selectedIdentity.Key)
            throw new InvalidOperationException("This Scout has results for another catalog identity. Create a new Scout to change providers; its old results and move history are retained.");
        subtitles?.UseReviewedReferences(previous);
        var contentOptions=settings.ContentOptions??ScoutContentOptions.Read(previous);
        var allEpisodes=episodes;
        if(contentOptions is {IncludeBonusContent:false}) {
            episodes=episodes.Where(e=>e.Season>0).ToList();
            if(episodes.Count==0)throw new InvalidOperationException("Enable bonus content to identify a guide containing only specials.");
        }
        var identificationScope=continuing
            ?IdentificationScope.Read(previous)??settings.Scope??new("legacy")
            :settings.Scope??new(settings.SelectedFiles==null?"show":"files");
        MoveExecutor.RequireSettled(previous);
        var relocations=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        if(continuing&&previous.Array("files").Count>0) {
            relocations=await RecordingRelocation.ReconnectAsync(previous,settings.Root,settings.Ffmpeg,progress,ct);
            Data.Save(reportPath,previous);
        }
        var dialogueInput=continuing&&ScoutStorage.Exists(reportPath)?reportPath:settings.DialogueInput;
        var cachedRows=dialogueInput is null?new Dictionary<string,JsonObject>(StringComparer.OrdinalIgnoreCase):
            Data.Read(dialogueInput).Array("files").ToDictionary(n=>Path.GetFullPath(n.Text("path")),n=>n!.AsObject(),StringComparer.OrdinalIgnoreCase);
        var paths=RecordingRelocation.MapSelection(previous,(settings.SelectedFiles??VideoSelection.Enumerate(settings.Root)).Concat(previous.Array("files").Select(r=>r.Text("path"))));
        var issues=settings.ReviewIssues||settings.Restart&&!settings.Reanalyze&&previous["issue_review"].Bool("active");
        var issuePaths=settings.ReviewIssues?IssueReview.Select(previous,contentOptions:contentOptions):previous["issue_review"].Array("paths").Select(p=>p!.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(settings.ReviewIssues)foreach(var path in paths.Where(p=>!cachedRows.ContainsKey(Path.GetFullPath(p))))issuePaths.Add(Path.GetFullPath(path));
        if(issues) {
            if(issuePaths.Count==0){progress.Report("No issues found. Saved results are unchanged.");return reportPath;}
            paths=issuePaths.Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        var incremental=settings.NewFilesOnly||settings.Restart&&!settings.Reanalyze&&!settings.ReviewIssues&&previous["incremental_review"].Bool("active");
        if(settings.NewFilesOnly&&previous["incremental_review"].Bool("active"))
            throw new InvalidOperationException("Resume Scout to finish the pending new files before scanning for more.");
        if(incremental) {
            paths=settings.NewFilesOnly
                ?VideoSelection.Enumerate(settings.Root).Where(p=>!cachedRows.ContainsKey(Path.GetFullPath(p))).ToList()
                :RecordingRelocation.MapSelection(previous,previous["incremental_review"].Array("paths").Select(p=>p!.GetValue<string>()));
            if(paths.Count==0){progress.Report("No new video files found. Saved analysis is unchanged.");return reportPath;}
        }
        var movedOut=previous.Array("files").Select(r=>r.Text("path")).Where(p=>RecordingRelocation.MovedOutsideSource(previous,p,settings.Root)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        paths=paths.Where(p=>!movedOut.Contains(p)).ToList();
        if(paths.Count==0&&movedOut.Count>0){progress.Report("No selected files remain in the source folder. Moved episode results are retained.");return reportPath;}
        var scoped=issues||incremental;
        if(paths.Any(p=>(!continuing&&!File.Exists(p))||!PreviewPlanner.Within(p,settings.Root))||paths.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=paths.Count)throw new InvalidDataException("Selected files must be distinct existing videos within this Scout.");
        if(paths.Count==0)throw new InvalidOperationException("No supported video files.");
        ScoutStorage.EnsureDirectory(settings.Output);var cache=Path.Combine(settings.Output,"reasoning-cache");ScoutStorage.EnsureDirectory(cache);
        var selected=paths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows=new JsonArray(previous.Array("files").Where(r=>scoped||movedOut.Contains(r.Text("path"))||selected.Contains(Path.GetFullPath(r.Text("path")))).Select(r=>r!.DeepClone()).ToArray());int index=0;
        var generation=settings.Reanalyze||settings.ReviewIssues?Guid.NewGuid().ToString("N"):previous.Text("analysis_generation");
        var review=issues?new JsonObject{["active"]=true,["paths"]=JsonSerializer.SerializeToNode(paths,Data.Options)}:null;
        var incrementalReview=incremental?new JsonObject{["active"]=true,["paths"]=JsonSerializer.SerializeToNode(paths,Data.Options)}:null;
        var retainedSplits=previous.Array("split_assignments").DeepClone().AsArray();
        if(settings.Reanalyze||settings.ReviewIssues) {
            var keys=rows.OfType<JsonObject>().Where(r=>selected.Contains(Path.GetFullPath(r.Text("path")))).Select(r=>CollectionResolver.FingerprintKey(r["primary_fingerprint"])).ToHashSet();
            foreach(var link in retainedSplits.ToList())if(keys.Contains(link.Text("fingerprint_a"))||keys.Contains(link.Text("fingerprint_b")))retainedSplits.Remove(link);
        }
        void SaveCheckpoint()=>Data.Save(reportPath,new JsonObject {["files"]=rows.DeepClone(),["source_root"]=Path.GetFullPath(settings.Root),["destination_root"]=previous["destination_root"]?.DeepClone(),["identification_scope"]=JsonSerializer.SerializeToNode(identificationScope,Data.Options),["moves"]=previous.Array("moves").DeepClone(),["analysis_generation"]=generation,["issue_review"]=review?.DeepClone(),["incremental_review"]=incrementalReview?.DeepClone(),["split_assignments"]=retainedSplits.DeepClone(),["series_identity"]=JsonSerializer.SerializeToNode(settings.SeriesIdentity,Data.Options),["pipeline"]=DialogueMatcher.Version,["content_options"]=JsonSerializer.SerializeToNode(contentOptions,Data.Options),["human_resolutions"]=previous.Array("human_resolutions").DeepClone(),["manual_corrections"]=previous.Array("human_resolutions").Any(r=>!r.Bool("undone"))});
        if(settings.Reanalyze||settings.ReviewIssues) {
            ct.ThrowIfCancellationRequested();
            Data.Save(Path.Combine(settings.Output,"analysis-history",generation,"report.json"),previous);
            var statuses=CollectionResolver.Resolve(previous).SelectMany(g=>g.Rows.Select(r=>(Path:Path.GetFullPath(r.Text("path")),g.Status))).ToDictionary(x=>x.Path,x=>x.Status,StringComparer.OrdinalIgnoreCase);
            foreach(var old in rows.OfType<JsonObject>().Where(r=>selected.Contains(Path.GetFullPath(r.Text("path"))))) {
                if(issues)old["issue_review_reason"]=statuses.GetValueOrDefault(Path.GetFullPath(old.Text("path")),"unfinished");
                old.Remove("reasoning");old.Remove("subtitle_match");old.Remove("conflict_review");old.Remove("conflict_review_pending");old.Remove("conflict_review_error");old.Remove("claim_resolution");old.Remove("claim_review_pending");old.Remove("claim_review_error");
                old["candidates"]=new JsonArray();old["error"]="Awaiting fresh analysis";old["status"]="analysis_pending";
            }
            SaveCheckpoint();
        }
        if(incremental)SaveCheckpoint();
        void SaveRow(JsonObject row) {
            var existing=rows.FirstOrDefault(r=>string.Equals(Path.GetFullPath(r.Text("path")),Path.GetFullPath(row.Text("path")),StringComparison.OrdinalIgnoreCase));
            if(existing!=null)rows[rows.IndexOf(existing)]=row;else rows.Add(row);
            SaveCheckpoint();
        }
        var prepared=new Dictionary<string,JsonObject>(StringComparer.OrdinalIgnoreCase);
        var preparationErrors=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string,CompilationProof> compilations=new Dictionary<string,CompilationProof>();
        if(contentOptions!=null) {
            int scanned=0;
            foreach(var path in paths) {
                pause?.Checkpoint();ct.ThrowIfCancellationRequested();
                progress.Report($"Reading dialogue [{++scanned}/{paths.Count}] {Path.GetFileName(path)}");
                if(cachedRows.TryGetValue(path,out var old)&&old.Text("relocation_error")!="")continue;
                if(old!=null&&!settings.Reanalyze&&!settings.ReviewIssues&&settings.Restart&&RunRecovery.CanReuse(old,episodes,settings.SeriesIdentity))continue;
                var saved=rows.OfType<JsonObject>().FirstOrDefault(r=>PreviewPlanner.Same(r.Text("path"),path))?.DeepClone().AsObject()
                    ??new JsonObject{["path"]=path,["candidates"]=new JsonArray()};
                try {
                    var evidence=await PrepareAsync(path,old,settings,progress,ct,pause);
                    if(DialogueAvailability.Unavailable(evidence)) {
                        foreach(var field in new[]{"original_path","path_history"})if(saved[field]!=null)evidence[field]=saved[field]!.DeepClone();
                        SaveRow(evidence);preparationErrors.Add(path);
                        progress.Report("Not processed: "+DialogueAvailability.Message(evidence));continue;
                    }
                    if(old!=null&&!CompilationEvidence.SourceUnchanged(old)) {
                        saved["candidates"]=new JsonArray();saved.Remove("reasoning");saved.Remove("subtitle_match");saved.Remove("content_excluded");
                    } else if(old!=null&&contentOptions.IncludeBonusContent==false&&ScoutContentOptions.KnownBonus(old)) {
                        // Preserve a verified excluded classification even when the
                        // requested reanalysis reset ordinary episode suggestions.
                        saved["candidates"]=old["candidates"]?.DeepClone();saved["reasoning"]=old["reasoning"]?.DeepClone();
                        saved.Remove("error");
                    }
                    foreach(var field in EvidenceFields)saved[field]=evidence[field]?.DeepClone();
                    saved["full_dialogue"]=true;prepared[path]=evidence;
                }catch(OperationCanceledException){throw;}
                catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException or FormatException or System.ComponentModel.Win32Exception) {
                    saved["error"]=ex.Message;saved["status"]="error";saved["candidates"]=new JsonArray();saved.Remove("reasoning");saved.Remove("dialogue");
                    preparationErrors.Add(path);progress.Report("Held for review: "+ex.Message);
                }
                SaveRow(saved);
            }
            compilations=CompilationEvidence.Find(rows.OfType<JsonObject>()).Where(pair=>!rows.OfType<JsonObject>().Any(r=>
                PreviewPlanner.Same(r.Text("path"),pair.Key)&&HumanResolution.For(previous,r)!=null)).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.OrdinalIgnoreCase);
            paths=paths.OrderBy(p=>compilations.ContainsKey(p)?3:cachedRows.TryGetValue(p,out var old)&&ScoutContentOptions.KnownBonus(old)?2:
                prepared.TryGetValue(p,out var ready)&&(ready["duration"]?.GetValue<double>()??0)<600?1:0)
                .ThenBy(p=>prepared.TryGetValue(p,out var ready)?ready["duration"]?.GetValue<double>()??0:0).ThenBy(p=>p,StringComparer.OrdinalIgnoreCase).ToList();
            progress.Report("Identifying episodes first. Short clips and optional content follow; duration alone never excludes a recording.");
        }
        var subtitleFirst=subtitles!=null&&settings.SeriesIdentity!=null||speech!=null||!settings.UseAiFallback;
        var titlePaths=new List<string>();var aiPaths=new List<string>();
        bool hasTitleCandidates=paths.Any(p=>TitleCandidates.Find(p,episodes)!=null);
        // Every recording gets its cheaper evidence checks before broad AI search.
        // Phase 0 never invokes AI, including when title hints are available.
        foreach(var phase in !settings.UseAiFallback?new[]{0}:subtitleFirst?(hasTitleCandidates?new[]{0,1,2}:new[]{0,2}):(hasTitleCandidates?new[]{1,2}:new[]{2})) {
        bool subtitlePass=phase==0,titlePass=phase==1;
        var batch=phase==2&&(subtitleFirst||hasTitleCandidates)?aiPaths:titlePass&&subtitleFirst?titlePaths:paths;
        if(batch.Count==0)continue;
        index=0;
        if(subtitlePass)progress.Report($"Subtitle pass: checking {batch.Count} recordings"+(settings.UseAiFallback?" before any AI fallback.":"; AI fallback is off for this Scout."));
        else if(titlePass)progress.Report($"Title verification: checking existing episode-title candidates for {batch.Count} unresolved recordings before full AI identification.");
        else if(subtitleFirst||hasTitleCandidates)progress.Report($"Subtitle pass complete. AI fallback: {batch.Count} unresolved recordings remain after title verification.");
        foreach(var path in batch) {
            pause?.Checkpoint();ct.ThrowIfCancellationRequested();
            var sourceLabel=$"[{++index}/{batch.Count}] {Path.GetFileName(path)}";
            if(!subtitlePass&&(subtitleFirst||hasTitleCandidates))sourceLabel=(titlePass?"Title verification ":"AI fallback ")+sourceLabel;
            progress.Report(sourceLabel);
            if(preparationErrors.Contains(path))continue;
            var savedRow=rows.OfType<JsonObject>().FirstOrDefault(r=>PreviewPlanner.Same(r.Text("path"),path));
            var compilation=compilations.TryGetValue(path,out var proof)&&savedRow!=null&&CompilationEvidence.SourcesUnchanged(proof,savedRow,rows.OfType<JsonObject>())?proof:null;
            if(contentOptions!=null&&savedRow!=null&&
                (compilation!=null&&!contentOptions.IncludeCompilations||!contentOptions.IncludeBonusContent&&ScoutContentOptions.KnownBonus(savedRow)&&CompilationEvidence.SourceUnchanged(savedRow))) {
                var excluded=savedRow.DeepClone().AsObject();excluded["content_excluded"]=compilation!=null?"compilation":"bonus";
                excluded["status"]="not_included";SaveRow(excluded);
                progress.Report(compilation!=null?"Play-all compilation left for later; individual recordings keep their episode matches.":"Bonus content left for a later run.");continue;
            }
            using var modelSource=matcher.ForSource(sourceLabel);
            if(cachedRows.TryGetValue(Path.GetFullPath(path),out var relocated)&&relocated.Text("relocation_error")!="") {SaveRow(relocated.DeepClone().AsObject());progress.Report(relocated.Text("relocation_error"));continue;}
            if(!settings.Reanalyze&&!settings.ReviewIssues&&settings.Restart&&cachedRows.TryGetValue(Path.GetFullPath(path),out var completed)&&RunRecovery.CanReuse(completed,episodes,settings.SeriesIdentity)) {
                progress.Report("Reusing successful identification and verified source fingerprints.");ReportCandidate(completed,progress,reused:true);continue;
            }
            if(titlePass&&TitleCandidates.Find(path,episodes)==null){aiPaths.Add(path);continue;}
            var row=new JsonObject {["path"]=Path.GetFullPath(path),["matching_method"]="reasoning",["full_dialogue"]=true,["candidates"]=new JsonArray()};
            if(issues)row["issue_review_reason"]=rows.FirstOrDefault(r=>string.Equals(Path.GetFullPath(r.Text("path")),path,StringComparison.OrdinalIgnoreCase)).Text("issue_review_reason","unfinished or competing recording");
            try {
                long size=new FileInfo(path).Length,mtime=PreviewPlanner.MtimeNs(path);string sample=PreviewPlanner.SampledDigest(path);
                List<Cue> cues;
                cachedRows.TryGetValue(Path.GetFullPath(path),out var cached);
                if(prepared.TryGetValue(path,out var ready)&&CompilationEvidence.SourceUnchanged(ready)) {
                    cues=MultipartEvidence.Cues(ready);row["duration"]=ready["duration"]?.DeepClone();row["transcription"]=ready["transcription"]?.DeepClone();
                    foreach(var field in new[]{"title_candidate","title_verification_attempt"})if(ready[field]!=null)row[field]=ready[field]!.DeepClone();
                    progress.Report("Using prepared full dialogue.");
                } else {
                    var evidence=await PrepareAsync(path,cached,settings,progress,ct,pause);
                    foreach(var field in EvidenceFields)row[field]=evidence[field]?.DeepClone();
                    cues=MultipartEvidence.Cues(evidence);row["skip_reason"]=evidence["skip_reason"]?.DeepClone();
                }
                row["size"]=size;row["sampled_digest"]=sample;row["source_mtime_ns"]=mtime;
                if(cues.Count==0) {
                    if(!CompilationEvidence.SourceUnchanged(row))throw new IOException("Media changed while reading dialogue.");
                    DialogueAvailability.Mark(row,row.Text("skip_reason","empty_subtitles"));
                    SaveRow(row);progress.Report("Not processed: "+DialogueAvailability.Message(row));continue;
                }
                row["dialogue"]=new JsonObject {["cues"]=JsonSerializer.SerializeToNode(cues,Data.Options)};
                if(cached!=null&&RunRecovery.SourceUnchanged(cached))row["primary_fingerprint"]=cached["primary_fingerprint"]!.DeepClone();
                if(compilation==null) {
                using var extentScope=matcher.ForRecording(RecordingExtent.FromRow(row,new JsonObject{
                    ["files"]=rows.DeepClone(),["series_identity"]=JsonSerializer.SerializeToNode(settings.SeriesIdentity,Data.Options)},episodes));
                var key=CollectionResolver.Hash(JsonSerializer.Serialize(new{generation,issues,version=DialogueMatcher.Version,revision=DialogueMatcher.RequestRevision,scope=matcher.CacheScope,settings.Endpoint,cues,episodes,settings.Model}));
                var cachePath=Path.Combine(cache,key+".json");
                JsonObject? result=null;
                if(subtitles!=null&&settings.SeriesIdentity is {} series) {
                    try {
                        var attempt=await subtitles.IdentifyAsync(cues,episodes,series,progress,ct,new SubtitleSourceContext(Path.GetFileName(path)));
                        SubtitleResult.Apply(row,episodes,attempt);
                        if(attempt.Result!=null&&SubtitleResult.Valid(row,episodes,series)) {
                            result=attempt.Result;progress.Report("Subtitle dialogue supports "+string.Join(" / ",attempt.Result.Array("matches").Select(m=>{var e=episodes.Single(e=>e.Id==m.Int("episode_id"));return e.Code+" — "+e.Title;}))+"; verifying source file.");
                        }else progress.Report(SubtitleResult.FallbackSummary(attempt.Details)+
                            (!settings.UseAiFallback?" Left unresolved for later; AI fallback is off.":subtitlePass?" Deferring AI until the subtitle pass is complete.":" Using AI with the saved dialogue."));
                    }catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
                    catch(Exception ex) when(ex is IOException or InvalidDataException or HttpRequestException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException or OperationCanceledException) {
                        row["subtitle_match"]=new JsonObject{["accepted"]=false,["reason"]="Subtitle lookup or validation was unavailable."};
                        progress.Report(row["subtitle_match"].Text("reason")+(!settings.UseAiFallback?" Left unresolved for later; AI fallback is off.":subtitlePass?" Deferring AI until the subtitle pass is complete.":" Using AI with saved dialogue."));
                    }
                }
                var titleCandidate=TitleCandidates.Find(path,episodes);
                if(result==null&&titleCandidate!=null)row["title_candidate"]=JsonSerializer.SerializeToNode(titleCandidate,Data.Options);
                void Defer(List<string> pending) {
                    if(!CompilationEvidence.SourceUnchanged(row))throw new IOException("Media changed while checking dialogue.");
                    row["matching_method"]="reasoning";row.Remove("reasoning");row["candidates"]=new JsonArray();row["status"]="awaiting_ai";
                    if(cached!=null&&RunRecovery.SourceUnchanged(cached))foreach(var field in new[]{"original_path","path_history","duplicate_of"})if(cached[field]!=null)row[field]=cached[field]!.DeepClone();
                    prepared[path]=row.DeepClone().AsObject();SaveRow(row);pending.Add(path);
                }
                if(result==null&&subtitlePass) {Defer(hasTitleCandidates||!settings.UseAiFallback?titlePaths:aiPaths);continue;}
                if(result==null&&titlePass) {
                    // A previous competing claim or conflicting reference identity
                    // needs the full-guide review, not a filename-directed check.
                    bool conflict=issues&&cached.Array("candidates").Count>0||row["subtitle_match"].Bool("mapping_conflict");
                    if(titleCandidate!=null&&!conflict) {
                        var titleKey=TitleCandidates.Signature(titleCandidate,cues,episodes,matcher,settings.Model,settings.Endpoint,generation);
                        var titleCache=Path.Combine(cache,"title-candidates",titleKey+".json");
                        JsonObject? audit=null;
                        if(ScoutStorage.Exists(titleCache))try {audit=Data.Read(titleCache);TitleCandidates.Accepted(audit,titleCandidate,cues);}
                        catch(Exception ex) when(ex is IOException or InvalidDataException or JsonException or InvalidOperationException or ArgumentException){audit=null;}
                        try {
                            if(audit==null) {
                                progress.Report("Verifying title candidate against complete dialogue: "+string.Join(" / ",titleCandidate.Episodes.Select(e=>e.Code+" — "+e.Title))+"…");
                                audit=await matcher.VerifyTitleCandidateAsync(cues,titleCandidate,settings.Model,settings.Endpoint,ct);
                                Data.Save(titleCache,audit);
                            }else progress.Report("Reusing exact-input title verification.");
                            bool accepted=TitleCandidates.Accepted(audit,titleCandidate,cues);
                            row["title_verification_attempt"]=new JsonObject{["accepted"]=accepted,["result"]=audit.DeepClone(),["reason"]=accepted?"Complete dialogue supports the proposed episode title(s).":"Title verification did not establish the complete proposed recording; full identification is still needed."};
                            if(accepted)result=audit;
                            else if(audit.Bool("coverage_review_only")) {
                                result=audit;
                                row["title_verification_attempt"]!["reason"]="Story identified; complete multipart coverage needs review. Skipping a redundant full-catalog search.";
                                progress.Report("Story identified; complete multipart coverage needs review. Skipping a redundant full-catalog search.");
                            }
                        }catch(Exception ex) when(ex is InvalidDataException or JsonException or ModelContextException) {
                            row["title_verification_attempt"]=new JsonObject{["accepted"]=false,["reason"]="Title verification did not produce valid grounded evidence; full identification is still needed."};
                        }
                    }else if(conflict)row["title_verification_attempt"]=new JsonObject{["accepted"]=false,["reason"]="Competing claims or conflicting reference identities require full-guide review."};
                    if(result==null) {progress.Report("Deferring full identification until all title candidates have been checked.");Defer(aiPaths);continue;}
                }
                if(result==null) {
                    row["matching_method"]="reasoning";row.Remove("reasoning");row["candidates"]=new JsonArray();
                    if(ScoutStorage.Exists(cachePath)){result=Data.Read(cachePath);progress.Report("Reusing exact-input model result.");}
                    else{progress.Report("Reading full dialogue and independently verifying guide evidence…");result=issues?await matcher.ReevaluateConflictAsync(cues,episodes,settings.Model,settings.Endpoint,ct):await matcher.IdentifyAsync(cues,episodes,settings.Model,settings.Endpoint,ct);Data.Save(cachePath,result);}
                }
                // Cached subtitle results still belong to their cache document.
                row["reasoning"]=result.DeepClone();
                // Issue review already performed both independent full-guide passes.
                // Reuse that exact-input verification during collection reconciliation.
                if(issues&&result.Text("method")==DialogueMatcher.Version&&result["title_verification"]==null)row["conflict_review"]=new JsonObject{["signature"]=ConflictReview.Signature(cues,episodes,matcher,settings.Model,settings.Endpoint),
                    ["source"]="issue_review",["previous_reasoning"]=cached?["reasoning"]?.DeepClone(),["previous_candidates"]=cached?["candidates"]?.DeepClone()};
                row["candidates"]=new JsonArray(result.Array("matches").Select(n=>(JsonNode)new JsonObject{
                    ["episode"]=JsonSerializer.SerializeToNode(episodes.Single(e=>e.Id==n.Int("episode_id")),Data.Options),
                    ["method"]=row.Text("matching_method"),["reason"]=n!["reason"]?.DeepClone(),
                    ["evidence"]=new JsonArray(n.Array("evidence").Select(b=>(JsonNode)new JsonObject{["start"]=b!["start"]?.DeepClone(),["text"]=b["source_text"]?.DeepClone()}).ToArray())
                }).ToArray());
                } else {row["matching_method"]="compilation";row["candidates"]=new JsonArray();progress.Report("Verified play-all compilation; no episode claim or AI request needed.");}
                if(contentOptions is {IncludeBonusContent:false}&&ScoutContentOptions.KnownBonus(row)) {
                    row["content_excluded"]="bonus";row["status"]="not_included";SaveRow(row);continue;
                }
                if(cached!=null&&RunRecovery.SourceUnchanged(cached)) {
                    row["primary_fingerprint"]=cached["primary_fingerprint"]!.DeepClone();
                    progress.Report("Reusing verified primary video/audio fingerprints for unchanged media.");
                } else {
                progress.Report("Verifying complete primary video/audio hashes…");
                var hashes=await RunTool(settings.Ffmpeg,["-nostdin","-v","error","-i",path,"-map","0:v:0","-map","0:a:0","-c","copy","-f","streamhash","-hash","sha256","-"],ct);
                string StreamHash(string type)=>Regex.Match(hashes,@"\d+,"+type+@",SHA256=([0-9a-fA-F]{64})").Groups[1].Value.ToLowerInvariant();
                var video=StreamHash("v");var audio=StreamHash("a");
                if(video.Length!=64||audio.Length!=64)throw new InvalidDataException("Both primary stream hashes are required.");
                row["primary_fingerprint"]=new JsonObject {["algorithm"]="primary-packets-sha256-v1",["video"]=video,["audio"]=audio,["size"]=size,["mtime_ns"]=mtime,["sampled_digest"]=sample};
                }
                if(new FileInfo(path).Length!=size||PreviewPlanner.MtimeNs(path)!=mtime||PreviewPlanner.SampledDigest(path)!=sample)throw new IOException("Media changed while processing.");
                row["recording_id"]=CollectionResolver.Hash(CollectionResolver.FingerprintKey(row["primary_fingerprint"])!);
                if(cached!=null&&CollectionResolver.FingerprintKey(cached["primary_fingerprint"])==CollectionResolver.FingerprintKey(row["primary_fingerprint"]))
                    foreach(var field in new[]{"original_path","path_history","duplicate_of"})if(cached[field]!=null)row[field]=cached[field]!.DeepClone();
                row["status"]="needs_review";
            }catch(OperationCanceledException) when(ct.IsCancellationRequested){
                // Retain completed evidence and exact-input model caches, but never
                // present an interrupted file as a finished assignment.
                row["status"]="interrupted";row["error_code"]="processing_interrupted";
                row["error"]="Processing stopped by request. Resume Scout will retry this unfinished file.";
                row["candidates"]=new JsonArray();SaveRow(row);throw;
            }
            catch(OperationCanceledException){throw;}
            catch(UsageLimitException){throw;}
            catch(Exception ex) {if(ex is ModelRequestTimeoutException timeout){row["error_code"]="model_timeout";row["timeout_seconds"]=timeout.TimeoutSeconds;}if(ex.Data[AssignmentValidation.ReasonKey] is string issue)row["assignment_issue"]=issue;if(ex.Data["context_recovery"] is string contextRecovery)row["context_recovery_attempts"]=JsonNode.Parse(contextRecovery);if(ex.Data["model_attempts"] is string attempts)row["model_attempts"]=JsonNode.Parse(attempts);if(ex.Data["model_candidate_ids"] is string candidateIds)row["model_candidate_ids"]=JsonNode.Parse(candidateIds);row["error"]=ex.Message;row["status"]="error";row["candidates"]=new JsonArray();progress.Report("Held for review: "+ex.Message);}
            SaveRow(row);
            ReportCandidate(row,progress);
            pause?.Checkpoint();
        }
        }
        if(!settings.UseAiFallback)progress.Report($"Subtitle pass complete. {titlePaths.Count} recording(s) left unresolved for later. Enable Use AI for unresolved files, then choose Review issues when ready.");
        pause?.Checkpoint();
        // Reconcile only two distinct partial recordings claiming the same catalog episode.
        // Fingerprint duplicates are collapsed before any model comparison.
        var finalReport=Data.Read(Path.Combine(settings.Output,"report.json"));
        var splitAssignments=retainedSplits.DeepClone().AsArray();finalReport["split_assignments"]=splitAssignments;
        var groups=CollectionResolver.Resolve(finalReport);
        foreach(var contenders in groups.Where(g=>settings.UseAiFallback&&g.Claims.Count==1).GroupBy(g=>g.Claims[0].Id)) {
            var pair=contenders.ToList();
            if(scoped&&!pair.SelectMany(g=>g.Rows).Any(r=>selected.Contains(Path.GetFullPath(r.Text("path")))))continue;
            if(pair.Count!=2||pair.Any(g=>g.Rows.Any(r=>!MultipartEvidence.Partial(r)||r.Text("error")!=""||r.Bool("source_changed")||CollectionResolver.FingerprintKey(r["primary_fingerprint"])==null)))continue;
            pause?.Checkpoint();ct.ThrowIfCancellationRequested();progress.Report("Checking whether two recordings are complementary parts of "+pair[0].Claims[0].Code+"…");
            try {
                var savedLink=(settings.Reanalyze||settings.ReviewIssues?new JsonArray():previous.Array("split_assignments")).OfType<JsonObject>().FirstOrDefault(l=>MultipartEvidence.SplitLink(l,pair[0],pair[1],pair[0].Claims[0])||MultipartEvidence.SplitLink(l,pair[1],pair[0],pair[0].Claims[0]));
                var link=savedLink?.DeepClone().AsObject()??await matcher.VerifySplitAsync(pair[0].Rows[0],pair[1].Rows[0],pair[0].Claims[0],settings.Model,settings.Endpoint,ct);
                if(link!=null&&!splitAssignments.Any(existing=>JsonNode.DeepEquals(existing,link)))splitAssignments.Add(link);
            }catch(OperationCanceledException){throw;}
            catch(UsageLimitException){throw;}
            catch(Exception ex){progress.Report("Split verification unresolved: "+ex.Message);}
            Data.Save(Path.Combine(settings.Output,"report.json"),finalReport);
            pause?.Checkpoint();
        }
        Data.Save(Path.Combine(settings.Output,"report.json"),finalReport);
        if(settings.UseAiFallback) {
            await ConflictReview.RunAsync(finalReport,episodes,matcher,settings.Model,settings.Endpoint,()=>Data.Save(reportPath,finalReport),progress,ct,pause,allowedPaths:scoped?selected:null);
            if(issues)await ClaimReview.RunAsync(finalReport,episodes,matcher,settings.Model,settings.Endpoint,()=>Data.Save(reportPath,finalReport),progress,ct,pause,allowedPaths:selected);
        }else if(groups.Any(g=>g.Status is "unresolved_conflict" or "partial_unverified"))progress.Report("Competing or partial episode claims remain held; AI review is off for this Scout.");
        if(incremental){finalReport["incremental_review"]!["active"]=false;Data.Save(reportPath,finalReport);}
        if(issues){finalReport["issue_review"]!["active"]=false;Data.Save(reportPath,finalReport);}
        Data.Save(Path.Combine(settings.Output,"catalog.json"),new{series_identity=settings.SeriesIdentity,episodes=allEpisodes});
        ReportResolvedMatches(finalReport,progress,scoped?selected:null);
        return Path.Combine(settings.Output,"report.json");
    }
}
