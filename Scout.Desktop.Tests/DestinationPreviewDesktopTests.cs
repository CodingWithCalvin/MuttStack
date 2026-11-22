using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Scout.Core;
using Scout.Desktop;

static class DestinationPreviewDesktopTests {
 public static void Run(MainWindow window,WorkspaceStore store) {
  const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
  object? Call(string name,params object[] args)=>typeof(MainWindow).GetMethod(name,flags)!.Invoke(window,args);
  void Set(string name,object? value)=>typeof(MainWindow).GetField(name,flags)!.SetValue(window,value);
  object? Get(string name)=>typeof(MainWindow).GetField(name,flags)!.GetValue(window);
  T C<T>(string name)=>(T)window.FindName(name);
  void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS "+message);}
  // Both source and target remain separate from Scout's private test storage.
  var library=Path.Combine(Path.GetDirectoryName(store.Root)!,"destination-preview-library");
  var root=Path.Combine(library,"Show");Directory.CreateDirectory(root);
  var source=Path.Combine(root,"old-name.mkv");File.WriteAllText(source,"unchanged source fixture");
  Episode[] catalog=[new(2345,1,1,"Opening")];
  var run=store.CreateRun(root);run.Name="Show";run.SeriesIdentity=new("tmdb","123","Show");
  var folder=store.DirectoryFor(run);run.Report=Path.Combine(folder,"report.json");run.Catalog=Path.Combine(folder,"catalog.json");store.Save(run);
  var report=new JsonObject{["files"]=new JsonArray(new JsonObject{["path"]=source,["matching_method"]="reasoning",
   ["candidates"]=new JsonArray(new JsonObject{["episode"]=JsonSerializer.SerializeToNode(catalog[0],Data.Options)}),
   ["primary_fingerprint"]=new JsonObject{["algorithm"]="full-file-sha256-v1",["sha256"]=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(source))),["size"]=new FileInfo(source).Length,["mtime_ns"]=PreviewPlanner.MtimeNs(source),["sampled_digest"]=PreviewPlanner.SampledDigest(source)}})};
  Data.Save(run.Report,report);Data.Save(run.Catalog,new{episodes=catalog});
  var reportBefore=ScoutStorage.ReadText(run.Report);
  Set("workspace",run);Set("report",report);Set("reportPath",run.Report);Set("catalogPath",run.Catalog);Set("outputPath",folder);
  C<TextBox>("LibraryPath").Text=root;C<TextBox>("SeriesName").Text="Show";
  var settings=new AppSettings{Naming=new(){PreserveCorrectNames=false}};Set("appSettings",settings);
  foreach(var mode in new[]{"custom","default"})foreach(var (kind,target) in new[]{("same",root),("nested",Path.Combine(root,"Formatted")),("parent",library)}) {
   run.DestinationMode=mode;run.DestinationTemplateOverride=mode=="custom"?DestinationTemplate.Literal(target):null;
   settings.DestinationTemplate=DestinationTemplate.Literal(target);Call("RefreshRunOptions");
   // Fail synchronously with a stack trace rather than opening a modal error
   // if the UI's destination validation regresses again.
   var resolved=(string?)Call("SelectedDestinationRoot");
   if(resolved==null||!PreviewPlanner.Same(resolved,target))throw new Exception("UI rejected the "+kind+" destination for "+mode);
   var context=SynchronizationContext.Current;
   try {
    SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
    Call("GeneratePreview",new Button{Content="Regenerate preview"},new RoutedEventArgs());
    var deadline=DateTime.UtcNow.AddSeconds(10);
    while(Get("operation")!=null&&DateTime.UtcNow<deadline){window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);Thread.Sleep(10);}
    if(Get("operation")!=null)throw new Exception("Preview did not finish.");
   }finally{SynchronizationContext.SetSynchronizationContext(context);}
   var plan=(MovePlan?)Get("currentMovePlan");
   Check(plan!=null&&PreviewPlanner.Same(plan.DestinationRoot,target)&&plan.Items.Single().Action=="rename/move"&&C<TextBlock>("Status").Text.StartsWith("Preview ready"),
    "Regenerate preview accepts the "+kind+" folder through "+mode+" destination settings");
  }
  Check(File.ReadAllText(source)=="unchanged source fixture"&&ScoutStorage.ReadText(run.Report)==reportBefore&&Directory.GetFiles(library,"*",SearchOption.AllDirectories).Length==1,
   "Same/nested/parent destination previews leave the saved report and source fixtures unchanged");
  run.DestinationMode="custom";run.DestinationTemplateOverride=DestinationTemplate.Literal(folder);Call("RefreshRunOptions");
  bool rejected=false;try{Call("SelectedDestinationRoot");}catch(TargetInvocationException ex) when(ex.InnerException is ArgumentException a&&a.Message.Contains("internal Scout storage")){rejected=true;}
  Check(rejected,"Destination validation still protects internal Scout storage");
  run.DestinationMode="source";run.DestinationTemplateOverride=null;Call("RefreshRunOptions");
 }
}
