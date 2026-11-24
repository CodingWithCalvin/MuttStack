namespace Scout.Core;

// One desktop instance owns the workers. Reservations include queued Scouts,
// so a second worker cannot scan files which another Scout is about to change.
public sealed class ScoutRunCoordinator {
 readonly object sync=new();
 readonly Dictionary<string,string> sources=new(StringComparer.Ordinal);
 readonly SemaphoreSlim workers=new(2,2), model=new(1,1);
 public int Count {get {lock(sync)return sources.Count;}}
 public IDisposable Reserve(string id,string root) {
  root=Path.GetFullPath(root);
  lock(sync) {
   if(sources.ContainsKey(id))throw new InvalidOperationException("This Scout is already active.");
   CheckPaths([root]);sources.Add(id,root);
  }
  return new Lease(()=>{lock(sync)sources.Remove(id);});
 }
 public void CheckPaths(IEnumerable<string> paths) {
  lock(sync)foreach(var path in paths)foreach(var source in sources.Values)
   if(PreviewPlanner.Within(path,source)||PreviewPlanner.Within(source,path))
    throw new InvalidOperationException("An active or queued Scout is using this source folder. Pause it before starting another Scout or moving files into or out of that folder.");
 }
 public Task<IDisposable> EnterWorkerAsync(ScoutPause pause,CancellationToken ct)=>EnterAsync(workers,pause,ct);
 public Task<IDisposable> EnterModelAsync(ScoutPause? pause,CancellationToken ct)=>EnterAsync(model,pause,ct);
 static async Task<IDisposable> EnterAsync(SemaphoreSlim semaphore,ScoutPause? pause,CancellationToken ct) {
  while(true) {
   ct.ThrowIfCancellationRequested();pause?.Checkpoint();
   if(!await semaphore.WaitAsync(TimeSpan.FromMilliseconds(100),ct).ConfigureAwait(false))continue;
   try {ct.ThrowIfCancellationRequested();pause?.Checkpoint();return new Lease(()=>semaphore.Release());}
   catch {semaphore.Release();throw;}
  }
 }
 sealed class Lease(Action release):IDisposable {
  Action? action=release;
  public void Dispose()=>Interlocked.Exchange(ref action,null)?.Invoke();
 }
 public ISpeechTranscriber CoordinateSpeech(ISpeechTranscriber speech)=>new CoordinatedSpeech(this,speech);
 sealed class CoordinatedSpeech(ScoutRunCoordinator owner,ISpeechTranscriber inner):ISpeechTranscriber {
  public string ConfigurationKey=>inner.ConfigurationKey;
  public async Task<SpeechTranscript> TranscribeAsync(SpeechRequest request,IProgress<string> progress,CancellationToken ct,ScoutPause? pause=null) {
   progress.Report("Waiting for the shared speech/model worker…");
   using var lease=await owner.EnterModelAsync(pause,ct).ConfigureAwait(false);
   progress.Report("Speech worker ready: "+Path.GetFileName(request.Path));
   return await inner.TranscribeAsync(request,progress,ct,pause).ConfigureAwait(false);
  }
 }
}
