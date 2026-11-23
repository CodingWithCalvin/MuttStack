using System.IO;
using System.Net.Http;
using System.Windows;
using Scout.Core;
using Scout.Core.Subtitles;

namespace Scout.Desktop;

public partial class MainWindow {
 readonly ScoutRunCoordinator runCoordinator=new();
 readonly Dictionary<string,ActiveScout> activeScouts=[];
 sealed class ActiveScout(RunWorkspace run,IDisposable reservation) {
  public RunWorkspace Run {get;}=run;
  public CancellationTokenSource Cancellation {get;}=new();
  public ScoutPause Pause {get;}=new();
  public IDisposable Reservation {get;}=reservation;
  public bool Forced,Finished;
  public ModelActivity? Activity;
  public string LastMessage="Queued — waiting for an available Scout worker.";
 }
 ActiveScout? SelectedWorker=>workspace!=null?activeScouts.GetValueOrDefault(workspace.Id):null;
 bool CanSwitchScout=>operation==null||SelectedWorker?.Cancellation==operation;
 // This executor seam also lets the published desktop harness exercise real UI
 // scheduling with synthetic reports, without models, network, or live media.
 internal Func<MatchSettings,IProgress<string>,CancellationToken,ScoutPause,Task<string>>? PipelineOverride {get;set;}
 void AttachSelectedWorker() {
  var job=SelectedWorker;operation=job?.Cancellation;pauseRequest=job?.Pause;forceStopRequested=job?.Forced??false;
  ClearModelActivity();SetBusy(job!=null);
  if(job!=null) {
   Status.Text=job.LastMessage;
   if(job.Activity!=null)RenderModelActivity(job.Activity);
   CancelButton.IsEnabled=!job.Pause.IsRequested&&!job.Cancellation.IsCancellationRequested;
  }
 }
 void WorkerLog(ActiveScout job,string line) {
  if(job.Finished)return;
  job.LastMessage=line;job.Run.Events.Add(DateTime.Now.ToString("HH:mm")+"  "+line);workspaces.Save(job.Run);
  if(workspace?.Id==job.Run.Id) {
   ActivityLog.AppendText(line+Environment.NewLine);ActivityLog.ScrollToEnd();SessionLog.Text=string.Join("\n\n",job.Run.Events);
   Status.Text=job.Forced?"Force stop requested — saving progress.":job.Pause.IsRequested?"Pause requested — finishing and saving the current file.":line;
  }
 }
 IProgress<string> WorkerProgress(ActiveScout job)=>new ModelMessageObserver(line=>{
  if(!Dispatcher.HasShutdownStarted)Dispatcher.Invoke(()=>WorkerLog(job,line));
 });
 sealed class ModelMessageObserver(Action<string> report):IProgress<string> {public void Report(string value)=>report(value);}
 IProgress<ModelActivity> WorkerActivity(ActiveScout job)=>new ModelActivityObserver(update=>{
  if(Dispatcher.HasShutdownStarted)return;
  Dispatcher.Invoke(()=>{
   if(job.Finished)return;
   job.Activity=update;
   var text=update.Heading+(update.Source.Length==0?"":" · "+update.Source)+" · "+update.Detail;
   if(!update.IsHeartbeat)WorkerLog(job,text);
   if(workspace?.Id==job.Run.Id){RenderModelActivity(update);if(!job.Forced&&!job.Pause.IsRequested)Status.Text=text;}
  });
 });
 async Task RunScoutWorkerAsync(ActiveScout job,AppSettings preferences,string? key,bool restart,bool reanalyze,bool reviewIssues,bool newFilesOnly) {
  var run=job.Run;var token=job.Cancellation.Token;var progress=WorkerProgress(job);
  LocalRuntime? runtime=null;DialogueMatcher? matcher=null;IDisposable? modelLease=null;
  var output=workspaces.DirectoryFor(run);
  try {
   using var slot=await runCoordinator.EnterWorkerAsync(job.Pause,token);
   run.Status="Running";WorkerLog(job,"Scout worker ready.");RefreshHistory();
   var guideTarget=ScoutStorage.Exists(run.Catalog)?run.Catalog:Path.Combine(output,"catalog.json");
   using(var metadataHttp=MetadataHttp()) {
    WorkerLog(job,"Checking the episode guide and retrieving missing descriptions…");
    var guide=await EpisodeGuidePreparation.EnsureAsync(guideTarget,run.SeriesIdentity,run.SeriesIdentity==null?null:CatalogClient(metadataHttp,run.SeriesIdentity.Provider),token);
    if(guide.Any(e=>string.IsNullOrWhiteSpace(e.Overview)))WorkerLog(job,"Some guide entries lack descriptions. Subtitle matching can use their provider titles; AI suggestions may be held.");
   }
   run.Catalog=workspaces.SaveCatalog(run,guideTarget);job.Pause.Checkpoint();
   if(workspace?.Id==run.Id)catalogPath=run.Catalog;
   WorkerLog(job,newFilesOnly?"Scanning for added videos; saved results will be retained.":reviewIssues?"Reviewing unresolved files using saved evidence.":reanalyze?$"Reanalyzing {run.SelectedFiles.Count} videos; reusing unchanged dialogue and fingerprints.":restart?$"Resuming {run.SelectedFiles.Count} selected videos; retaining successful results for unchanged files.":$"Identifying {run.SelectedFiles.Count} selected videos.");
   WorkerLog(job,run.UseAiFallback?$"AI fallback enabled: {preferences.Provider} / {preferences.Model}.":"AI fallback is off; unresolved files remain available for later review.");
   bool aiReady=false;
   async Task EnsureAi(CancellationToken ct) {
    if(aiReady)return;
    progress.Report("Waiting for the shared speech/model worker for AI fallback…");
    modelLease??=await runCoordinator.EnterModelAsync(job.Pause,ct);
    progress.Report("AI worker ready; preparing the configured model…");
    if(preferences.Provider=="managed") {
     if(!File.Exists(preferences.ModelFile)&&preferences.AutoDownloadMissing) {
      using var downloadHttp=new HttpClient{Timeout=TimeSpan.FromHours(12)};
      preferences.ModelFile=await ModelDownloads.DownloadAsync(ModelDownloads.Profiles.Single(p=>p.Id==preferences.DownloadProfile),preferences,downloadHttp,progress,ct);
     }
     runtime=await LocalRuntime.StartAsync(preferences,progress,ct);
    }
    aiReady=true;
   }
   string endpoint=preferences.Provider=="managed"?$"http://127.0.0.1:{new Uri(preferences.LocalEndpoint).Port}/v1/chat/completions":preferences.Endpoint;
   var settings=new MatchSettings(run.Library,run.Catalog,output,preferences.Model,endpoint,preferences.Ffmpeg,preferences.Ffprobe,string.IsNullOrWhiteSpace(run.Dialogue)?null:run.Dialogue,run.SelectedFiles.ToList(),run.SeriesIdentity,restart,reanalyze,reviewIssues,newFilesOnly,run.SelectionScope,run.ContentOptions,run.UseAiFallback);
   using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(preferences.EffectiveTimeoutSeconds)};
   matcher=new(http,preferences.Inference(key),EnsureAi,WorkerActivity(job));
   SubtitleMatcher? subtitleMatcher=null;
   if(preferences.UseSubtitleMatching) {
    var cache=Path.Combine(Environment.GetEnvironmentVariable("SCOUT_SETTINGS")??Path.Combine(SettingsStore.DefaultDirectory,"UI-Trial"),"SubtitleCache");
    subtitleMatcher=new(new SubdlReferences(cache,settingsStore.ReadKey(SubtitleCredentials.Subdl),run.SeriesIdentity?.Provider=="tmdb"?settingsStore.ReadKey("metadata|tmdb"):null),cache);
   }
   var pipeline=new MediaPipeline(matcher,subtitles:subtitleMatcher,speech:preferences.Speech.Enabled?runCoordinator.CoordinateSpeech(new SpeechTranscriber(preferences.Speech,preferences.ModelDirectory)):null);
   var executor=PipelineOverride;
   var path=await Task.Run(()=>executor!=null?executor(settings,progress,token,job.Pause):pipeline.RunAsync(settings,progress,token,job.Pause),token);
   run.Report=path;run.Status="Ready for review";WorkerLog(job,"Matching complete. Results are ready; unresolved files remain available for review.");
  }catch(ScoutPausedException){run.Status="Paused";WorkerLog(job,"Scout paused at a saved checkpoint. Resume Scout will reuse completed results.");}
  catch(OperationCanceledException) when(token.IsCancellationRequested){run.Status="Paused";WorkerLog(job,"Scout stopped by request. Completed results are saved.");}
  catch(Exception ex){run.Status="Needs attention";WorkerLog(job,"Scout needs attention: "+ex.Message+" Saved progress is retained; resume to retry unfinished files.");}
  finally {
   try {if(runtime!=null)await runtime.DisposeAsync();}
   catch(Exception ex){run.Status="Needs attention";WorkerLog(job,"Could not finish model cleanup: "+ex.Message);}
   finally {modelLease?.Dispose();}
   if(matcher!=null)try{
    var usage=matcher.Usage;Data.Save(Path.Combine(output,"usage.json"),usage);
    UsageText.Text=$"{run.Name}: {usage.Requests} requests · {usage.InputTokens:N0} reported input tokens · {usage.OutputTokens:N0} reported output tokens · {usage.Provider}";
   }catch(Exception ex){WorkerLog(job,"Could not save usage summary: "+ex.Message);}
   job.Finished=true;activeScouts.Remove(run.Id);job.Reservation.Dispose();job.Cancellation.Dispose();workspaces.Save(run);
   if(workspace?.Id==run.Id) {
    operation=null;pauseRequest=null;forceStopRequested=false;
    // Loading an existing run must not import it as a new Scout.
    OpenSavedRun(run);
    if(run.Status=="Ready for review")ShowPage("Results");
    if(run.Status=="Paused"){PauseNotice.Text=job.LastMessage;PauseNotice.Visibility=Visibility.Visible;}
    Status.Text=job.LastMessage;
   }
   SetBusy(operation!=null);RefreshHistory();
  }
 }
}
