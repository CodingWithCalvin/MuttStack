using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
namespace Tucker;

public sealed record Receipt(string Id, string Digest, DateTime SavedUtc, int Files);
public sealed class QueueState
{
    public int Version { get; set; } = 1;
    public List<Job> Jobs { get; set; } = new();
    public List<Receipt> Receipts { get; set; } = new();
    public HashSet<string> ImportedSources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ArchivedRuns { get; set; } = new(StringComparer.Ordinal);
}

// All mutations are serialized on the caller's synchronization context (the WPF dispatcher).
public sealed class QueueManager
{
    readonly string path;
    public DoggoDatabase Database { get; }
    CancellationTokenSource? cancel;
    readonly HashSet<string> scheduled = new();
    public QueueState State { get; private set; }
    public bool Running { get; private set; }
    public bool PausePending { get; private set; }
    public bool CanJoinRunning => Running && !PausePending && cancel is {IsCancellationRequested:false};
    public Job? Current { get; private set; }
    public event Action? Changed;
    public event Action<string>? Line;
    public Task? RunTask { get; private set; }
    public QueueManager(string directory, string? databasePath = null)
    {
        path = Path.Combine(directory, "queues.json");
        Database = new DoggoDatabase(databasePath ?? Path.Combine(directory,"doggo.db"));
        State = Database.Read();
        if (State.Version != 1) throw new IOException("Unsupported queue-state version. Saved files were kept.");
        State.ImportedSources = new(State.ImportedSources, StringComparer.OrdinalIgnoreCase);
        foreach (var j in State.Jobs) {
            if (j.Status == "Publishing") { j.Status = "Needs review"; j.Error = "Publication was interrupted. Check the output path before explicitly importing again; automatic retry is blocked."; }
            else if (j.Status is "Encoding" or "Queued") j.Status = "Paused";
        }
    }
    public static string SourceKey(Job j) => Path.GetFullPath(j.Source).ToUpperInvariant()+"|"+j.SourceSize+"|"+j.SourceModified.Ticks;
    public static HashSet<string> ReacceptedSourceKeys(IEnumerable<Handoff> batches) => batches.Where(h=>h.Reaccept).SelectMany(h=>h.Manifest.Files).Select(f=>Doggo.DoggoInbox.SourceKey(f.Source,f.SourceSize,f.SourceModified)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    public string ImportStatus(Job job,IReadOnlySet<string> reaccepted) => ImportStatus(job,reaccepted,State);
    string ImportStatus(Job job,IReadOnlySet<string> reaccepted,QueueState state)
    {
        var key=SourceKey(job);
        if(state.Jobs.Any(j=>SourceKey(j)==key&&j.Status!="Completed"))return "Already queued";
        if(reaccepted.Contains(key)||job.Reprocess)return "Ready to reprocess";
        return state.ImportedSources.Contains(key)||Database.CompletedInRipley(key)?"Already imported":"Ready";
    }
    public void Save() => Database.Save(State);
    public int Add(IEnumerable<Job> preview, bool joinRunning, Handoff? handoff = null)
        => AddCore(preview,joinRunning,handoff==null?[]:[handoff]);
    public (int Files,string Id) AddRun(IEnumerable<Job> preview,bool joinRunning,IReadOnlyList<Handoff> batches,string name)
    {
        var id=Guid.NewGuid().ToString("N");
        var candidates=preview.ToList();
        var automatic=RunNames.Unique(RunNames.Suggest(candidates,[],batches),State.Jobs.Select(j=>j.QueueName));
        var files=AddCore(candidates,joinRunning,batches,id,string.IsNullOrWhiteSpace(name)?automatic:name.Trim());
        return (files,id);
    }
    public int AppendRun(string runId,IEnumerable<Job> preview,bool joinRunning,IReadOnlyList<Handoff> batches)
    {
        if(IsArchived(runId))throw new IOException("Restore this Tucker before adding sources.");
        var run=State.Jobs.FirstOrDefault(j=>j.QueueId==runId)??throw new IOException("This Tucker is no longer in history. Choose an existing run before adding sources.");
        return AddCore(preview,joinRunning,batches,run.QueueId,run.QueueName);
    }
    int AddCore(IEnumerable<Job> preview,bool joinRunning,IReadOnlyList<Handoff> handoffs,string? runId=null,string? runName=null)
    {
        if (joinRunning && !CanJoinRunning) throw new IOException("Active processing has finished, is pausing, or is stopping. Save these files as waiting instead.");
        var candidates=preview.ToList();
        if(handoffs.GroupBy(h=>h.Manifest.Id).Any(g=>g.Select(h=>(h.Digest,h.Reaccept)).Distinct().Count()>1))throw new IOException("A handoff has conflicting selections. Refresh the inbox.");
        var batches=handoffs.DistinctBy(h=>h.Manifest.Id).ToArray();
        foreach(var handoff in batches) {
            var previous=State.Receipts.FirstOrDefault(r=>r.Id==handoff.Manifest.Id);
            if(handoff.Reaccept&&previous==null)throw new IOException("Only an accepted inbox batch can be re-accepted. Refresh the inbox.");
            if(previous!=null) {
                if(previous.Digest!=handoff.Digest)throw new IOException("This handoff ID already exists with different contents.");
                if(!handoff.Reaccept) {
                    if(runId==null&&batches.Length==1)return 0;
                    throw new IOException("A selected inbox batch was already accepted. Use Re-accept selected in Inbox to process it again.");
                }
            }
            if(handoff.Manifest.Files.Any(f=>!candidates.Any(j=>j.Include&&SourceKey(j)==Doggo.DoggoInbox.SourceKey(f.Source,f.SourceSize,f.SourceModified))))
                throw new IOException("Accept every file in each selected inbox batch together. Preview the complete run again.");
        }
        var next = JsonSerializer.Deserialize<QueueState>(JsonSerializer.Serialize(State))!;
        next.ImportedSources = new(next.ImportedSources, StringComparer.OrdinalIgnoreCase);
        var reaccepted=ReacceptedSourceKeys(batches);
        var added = new List<Job>();
        foreach (var original in candidates.Where(j => j.Include)) {
            var key = SourceKey(original);
            if(runId!=null||reaccepted.Contains(key)||original.Reprocess) {
                var source=new FileInfo(original.Source);
                if(!source.Exists||source.Length!=original.SourceSize||source.LastWriteTimeUtc!=original.SourceModified)throw new IOException("A source changed after preview. Preview the run again: "+original.Source);
            }
            if (ImportStatus(original,reaccepted,next) is "Already imported" or "Already queued") continue;
            var j = JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(original))!;
            if(reaccepted.Contains(key)||original.Reprocess){j.Id=Guid.NewGuid().ToString("N");j.Error="";}
            if (j.Encoding == null) throw new IOException("Missing frozen encoding settings.");
            var reserved = next.Jobs.Where(x => string.Equals(x.Encoding?.Output, j.Encoding.Output, StringComparison.OrdinalIgnoreCase)).Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            j.Name = Engine.Unique(j.Encoding.Output, j.Name, reserved);
            j.Status = "Waiting";
            if(runId!=null){j.QueueId=runId;j.QueueName=runName!;}
            next.Jobs.Add(j); next.ImportedSources.Add(key); added.Add(j);
        }
        var addedKeys=added.Select(SourceKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var handoff in batches.Where(h=>!h.Reaccept))next.Receipts.Add(new(handoff.Manifest.Id,handoff.Digest,DateTime.UtcNow,handoff.Manifest.Files.Select(f=>Doggo.DoggoInbox.SourceKey(f.Source,f.SourceSize,f.SourceModified)).Distinct(StringComparer.OrdinalIgnoreCase).Count(addedKeys.Contains)));
        Database.Save(next,batches.Where(h=>h.Reaccept).ToArray()); // No acknowledgement or in-memory acceptance until the full state is durable.
        // Preserve references used by the active encode while adopting the durable transaction.
        State.Jobs.AddRange(added); State.ImportedSources = next.ImportedSources; State.Receipts = next.Receipts;
        if (joinRunning) foreach (var j in added) scheduled.Add(j.Id);
        Changed?.Invoke(); return added.Count;
    }
    public void Clear(string? queueId)
    {
        if(queueId!=null){DeleteRun(queueId);return;}
        if (Running) throw new IOException("Pause or stop processing before clearing queues.");
        var next = new QueueState { Jobs = State.Jobs.Where(j => queueId != null && j.QueueId != queueId).ToList(), Receipts = State.Receipts, ImportedSources = State.ImportedSources };
        Database.Save(next); State = next; Changed?.Invoke();
    }
    public bool IsArchived(string runId)=>State.ArchivedRuns.Contains(runId);
    public bool CanManageRun(string runId)=>State.Jobs.Any(j=>j.QueueId==runId)&&Current?.QueueId!=runId&&!State.Jobs.Any(j=>j.QueueId==runId&&scheduled.Contains(j.Id));
    QueueState CopyState()=>new(){Jobs=State.Jobs.ToList(),Receipts=State.Receipts.ToList(),ImportedSources=new(State.ImportedSources,StringComparer.OrdinalIgnoreCase),ArchivedRuns=new(State.ArchivedRuns,StringComparer.Ordinal)};
    void RequireManageable(string runId){if(!CanManageRun(runId))throw new IOException("Pause or stop processing this Tucker before archiving or deleting it. The run must still exist in history.");}
    public void SetArchived(string runId,bool archived)
    {
        RequireManageable(runId);var next=CopyState();
        if(archived)next.ArchivedRuns.Add(runId);else next.ArchivedRuns.Remove(runId);
        Database.Save(next);State=next;Changed?.Invoke();
    }
    public void DeleteRun(string runId)
    {
        RequireManageable(runId);var next=CopyState();next.Jobs.RemoveAll(j=>j.QueueId==runId);next.ArchivedRuns.Remove(runId);
        Database.Save(next);State=next;Changed?.Invoke();
    }
    public void TogglePause() { if (Running) { PausePending = !PausePending; Changed?.Invoke(); } }
    public void Stop() { cancel?.Cancel(); PausePending = false; Changed?.Invoke(); }
    public Task Start(string? queueId)
    {
        if(queueId!=null&&IsArchived(queueId))throw new IOException("Restore this Tucker before starting it.");
        if (Running) return RunTask ?? Task.CompletedTask;
        scheduled.Clear();
        foreach (var j in State.Jobs.Where(j => !IsArchived(j.QueueId) && (queueId == null || j.QueueId == queueId) && j.Status is "Waiting" or "Paused" or "Failed")) scheduled.Add(j.Id);
        RunTask = Pump(); return RunTask;
    }
    async Task Pump()
    {
        Running = true; PausePending = false; cancel = new(); var token = cancel.Token; string active = "";
        Changed?.Invoke();
        try {
            while (!token.IsCancellationRequested) {
                var job = State.Jobs.FirstOrDefault(j => scheduled.Contains(j.Id) && j.QueueId == active) ?? State.Jobs.FirstOrDefault(j => scheduled.Contains(j.Id));
                if (job == null) break;
                scheduled.Remove(job.Id); active = job.QueueId; Current = job;
                var options = job.Encoding ?? throw new IOException("Missing queue settings.");
                job.Error = ""; job.Status = "Encoding"; Save(); Changed?.Invoke();
                try {
                    Database.Materialize(options);
                    await Presets.Validate(options, token);
                    Directory.CreateDirectory(options.Output);
                    var logs = Path.Combine(Path.GetDirectoryName(path)!, "logs"); Directory.CreateDirectory(logs);
                    using var log = new StreamWriter(Path.Combine(logs, job.Id + ".log"), true); var gate = new object();
                    // Capture the UI synchronization context for publication state changes.
                    var context = SynchronizationContext.Current;
                    void Publishing(string name) {
                        void Update() { job.Name = name; job.Status = "Publishing"; Save(); }
                        if (context == null || SynchronizationContext.Current == context) Update(); else context.Send(_ => Update(), null);
                    }
                    job.Name = await Engine.Encode(job, options, line => { lock(gate) log.WriteLine(line); Line?.Invoke(line); }, token, Publishing);
                    job.Status = "Completed";
                    try { QueueStore.Write(Path.Combine(options.Output, job.Name + ".ripley.json"), new { version = 2, series_identity = job.Series, season = job.Season, volume_number = job.VolumeNumber, disc_number = job.DiscNumber, disc_label = job.DiscLabel, source = job.Source, original_filename = job.OriginalFilename, disc_metadata = job.DiscMetadata, preset = job.PresetDisplay, output = job.OutputPath, producer = "Tucker" }); }
                    catch(Exception ex) { job.Error = "Video saved; metadata sidecar failed: " + ex.Message; }
                }
                catch (OperationCanceledException) { job.Status = "Paused"; }
                catch (Exception ex) { job.Status = job.Status == "Publishing" ? "Needs review" : "Failed"; job.Error = ex.Message; }
                Save(); Changed?.Invoke();
                if (PausePending) break;
            }
        }
        finally { Current = null; Running = false; PausePending = false; cancel.Dispose(); cancel = null; scheduled.Clear(); Changed?.Invoke(); }
    }
}

public sealed class HandoffManifest
{
    public int Version { get; set; } = 1;
    public string SourceApp { get; set; } = "ripley";
    public string TargetApp { get; set; } = "tucker";
    public string Id { get; set; } = "";
    public List<HandoffFile> Files { get; set; } = new();
}
public sealed class HandoffFile
{
    public string Source { get; set; } = "";
    public long SourceSize { get; set; }
    public DateTime SourceModified { get; set; }
    public SeriesIdentity? Series { get; set; }
    public string Season { get; set; } = "";
    public string VolumeNumber { get; set; } = "";
    public string DiscNumber { get; set; } = "";
    public string DiscLabel { get; set; } = "";
    public string DiscId { get; set; } = "";
    public DateTime RipDate { get; set; }
}
public sealed record Handoff(HandoffManifest Manifest, string Digest)
{
    // Explicit local draft intent; never changes the immutable producer manifest or receipt.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Reaccept { get; init; }
    public static Handoff Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var manifest = JsonSerializer.Deserialize<HandoffManifest>(bytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new IOException("Empty handoff.");
        if (manifest.TargetApp != "tucker" || manifest.Version != 1 || !Guid.TryParse(manifest.Id, out var id) || manifest.Files.Count == 0) throw new IOException("Handoff needs version 1, a GUID Id, and completed Files.");
        manifest.Id = id.ToString("D");
        return new(manifest, Convert.ToHexString(SHA256.HashData(bytes)));
    }
    public List<Job> Plan(Options options, string folderFormat, string prefix)
    {
        var result = new List<Job>();
        foreach (var file in Manifest.Files) {
            if (!Path.IsPathFullyQualified(file.Source) || !Path.GetExtension(file.Source).Equals(".mkv", StringComparison.OrdinalIgnoreCase) || RawRip.IsStagingFile(file.Source)) throw new IOException("Handoff source must be a completed absolute MKV path.");
            var stat = new FileInfo(file.Source);
            if (!stat.Exists || stat.Length != file.SourceSize || stat.LastWriteTimeUtc != file.SourceModified) throw new IOException("Handoff source changed or is missing: " + file.Source);
            var context = new FolderContext(file.Series, file.Season, file.DiscLabel, file.RipDate, file.VolumeNumber, file.DiscNumber);
            var output = FolderNames.Resolve(options.Output, folderFormat, context);
            if (file.Source.StartsWith(Path.TrimEndingDirectorySeparator(options.Output) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Source cannot be in the output tree.");
            var original = Path.GetFileName(file.Source);
            result.Add(new Job { Source = Path.GetFullPath(file.Source), SourceSize = file.SourceSize, SourceModified = file.SourceModified, OriginalFilename = original, Series = file.Series, Season = file.Season, VolumeNumber = file.VolumeNumber, DiscNumber = file.DiscNumber, DiscLabel = file.DiscLabel, RipDate = file.RipDate, QueueId = "handoff:" + Manifest.Id + ":" + file.DiscId, QueueName = file.DiscLabel, Name = Path.ChangeExtension(Manifest.SourceApp=="ripley"?original:FileNames.WithPrefix(prefix, original, context), options.Extension), Encoding = options with { Output = output } });
        }
        return result;
    }
}
