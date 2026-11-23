using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Scout.Core;
using Scout.Desktop;

static class ConcurrentScoutDesktopTests {
 public static void Run(MainWindow window,WorkspaceStore store) {
  const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
  object? Call(string name,params object[] args)=>typeof(MainWindow).GetMethod(name,flags)!.Invoke(window,args);
  T C<T>(string name)=>(T)window.FindName(name);
  void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS "+message);}
  void Until(Func<bool> condition) {
   var deadline=DateTime.UtcNow.AddSeconds(15);
   while(!condition()&&DateTime.UtcNow<deadline){window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);Thread.Sleep(10);}
   if(!condition())throw new Exception("Concurrent desktop fixture timed out.");
  }
  var context=SynchronizationContext.Current;
  var started=new System.Collections.Concurrent.ConcurrentDictionary<string,bool>();
  var releases=new Dictionary<string,TaskCompletionSource>();var workers=new List<Task>();
  var runs=Enumerable.Range(1,3).Select(i=>{
   var source=Path.Combine(Path.GetDirectoryName(store.Root)!,"concurrent-scout-"+i);Directory.CreateDirectory(source);
   var video=Path.Combine(source,"episode.mkv");File.WriteAllText(video,"synthetic source "+i);
   var run=store.CreateRun(source);run.Name="Concurrent "+i;run.Model="fixture";run.SelectedFiles=[video];
   run.Catalog=Path.Combine(store.DirectoryFor(run),"catalog.json");
   Data.Save(run.Catalog,new{episodes=new[]{new Episode(i,1,1,"Episode "+i,"A complete fixture description.")}});store.Save(run);
   releases.Add(source,new(TaskCreationOptions.RunContinuationsAsynchronously));return run;
  }).ToArray();
  try {
   SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
   Call("LoadSettings");C<CheckBox>("SubtitleMatchingEntry").IsChecked=true;C<CheckBox>("SpeechEnabledEntry").IsChecked=false;
   window.PipelineOverride=async(settings,progress,ct,pause)=>{
    started[settings.Root]=true;progress.Report("Owned progress: "+settings.Root);
    await releases[settings.Root].Task.WaitAsync(ct);pause.Checkpoint();
    if(settings.Root==runs[2].Library)throw new InvalidDataException("Synthetic worker failure");
    var path=Path.Combine(settings.Output,"report.json");Data.Save(path,new JsonObject{["files"]=new JsonArray(),["source_root"]=settings.Root});return path;
   };
   foreach(var run in runs) {
    Call("OpenSavedRun",run);workers.Add((Task)Call("IdentifyRunAsync",true,false,false,false)!);
   }
   Until(()=>started.Count==2);
   Check(C<ListBox>("HistoryList").IsEnabled&&store.ReadAll().Single(r=>r.Id==runs[2].Id).Status=="Queued",
    "Two Scouts process concurrently while a third queues and history stays available");
   Call("Cancel",new Button(),new RoutedEventArgs());Until(()=>workers[2].IsCompleted);
   workers[2].GetAwaiter().GetResult();
   Check(store.ReadAll().Single(r=>r.Id==runs[2].Id).Status=="Paused"&&started.Count==2&&!workers[0].IsCompleted&&!workers[1].IsCompleted,
    "Pausing the selected queued Scout leaves both background Scouts running");
   Call("OpenSavedRun",runs[0]);
   Check(C<ActivityLogView>("ActivityLog").Text.Contains("Owned progress: "+runs[0].Library)&&!C<ActivityLogView>("ActivityLog").Text.Contains("Owned progress: "+runs[1].Library),
    "Switching to an active Scout restores only its own progress and controls");
   releases[runs[1].Library].SetResult();Until(()=>workers[1].IsCompleted);workers[1].GetAwaiter().GetResult();
   var selected=(RunWorkspace)typeof(MainWindow).GetField("workspace",flags)!.GetValue(window)!;
   Check(selected.Id==runs[0].Id&&!workers[0].IsCompleted&&store.ReadAll().Single(r=>r.Id==runs[1].Id).Status=="Ready for review",
    "A background Scout saves completion without replacing the selected running Scout");
   Check(!C<TabControl>("SettingsTabs").IsEnabled,"Model settings stay fixed while any Scout worker is active");
   Call("OpenSavedRun",runs[2]);var retried=(Task)Call("IdentifyRunAsync",true,false,false,false)!;workers.Add(retried);
   Until(()=>started.ContainsKey(runs[2].Library));Call("OpenSavedRun",runs[0]);releases[runs[2].Library].SetResult();Until(()=>retried.IsCompleted);retried.GetAwaiter().GetResult();
   Check(store.ReadAll().Single(r=>r.Id==runs[2].Id).Status=="Needs attention"&&!workers[0].IsCompleted&&C<ActivityLogView>("ActivityLog").Text.Contains("Owned progress: "+runs[0].Library)&&!C<ActivityLogView>("ActivityLog").Text.Contains("Synthetic worker failure"),
    "A background worker failure is saved to its own Scout without interrupting the selected run");
   Call("ForceStop",new Button(),new RoutedEventArgs());Until(()=>workers[0].IsCompleted);workers[0].GetAwaiter().GetResult();
   Check(store.ReadAll().Single(r=>r.Id==runs[0].Id).Status=="Paused"&&C<TabControl>("SettingsTabs").IsEnabled,
    "Force stop cancels only the selected Scout and releases worker ownership");
   var saved=store.ReadAll();
   Check(runs.All(r=>saved.Single(s=>s.Id==r.Id).Events.All(e=>!e.Contains("Owned progress:")||e.Contains(r.Library)))&&runs.All(r=>File.ReadAllText(r.SelectedFiles[0]).StartsWith("synthetic source")),
    "Concurrent runs persist separate event histories and leave every media fixture untouched");
  }finally {
   foreach(var release in releases.Values)release.TrySetResult();
   if(workers.Any(t=>!t.IsCompleted))Until(()=>workers.All(t=>t.IsCompleted));
   window.PipelineOverride=null;SynchronizationContext.SetSynchronizationContext(context);
  }
 }
}
