using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Scout.Core;
using Scout.Desktop;

static class DestinationChoiceDesktopTests {
 public static void Run(MainWindow window,WorkspaceStore store) {
  const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
  object? Call(string name,params object[] args)=>typeof(MainWindow).GetMethod(name,flags)!.Invoke(window,args);
  void Set(string name,object? value)=>typeof(MainWindow).GetField(name,flags)!.SetValue(window,value);
  object? Get(string name)=>typeof(MainWindow).GetField(name,flags)!.GetValue(window);
  T C<T>(string name)=>(T)window.FindName(name);
  void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS "+message);}
  var root=Path.Combine(Path.GetDirectoryName(store.Root)!,"choice-media");Directory.CreateDirectory(root);
  var source=Path.Combine(root,"original.mkv");File.WriteAllText(source,"original fixture");
  var run=store.CreateRun(root);run.Name="Show";run.DestinationMode="source";run.SeriesIdentity=new("tmdb","123","Show");
  var folder=store.DirectoryFor(run);run.Report=Path.Combine(folder,"report.json");run.Catalog=Path.Combine(folder,"catalog.json");store.Save(run);
  Episode[] catalog=[new(1234,1,1,"Opening")];Data.Save(run.Catalog,new{episodes=catalog});
  var row=new JsonObject{["path"]=source,["matching_method"]="reasoning",["candidates"]=new JsonArray(new JsonObject{["episode"]=JsonSerializer.SerializeToNode(catalog[0],Data.Options)}),
   ["primary_fingerprint"]=new JsonObject{["algorithm"]="full-file-sha256-v1",["sha256"]=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(source))),["size"]=new FileInfo(source).Length,["mtime_ns"]=PreviewPlanner.MtimeNs(source),["sampled_digest"]=PreviewPlanner.SampledDigest(source)}};
  var report=new JsonObject{["files"]=new JsonArray(row)};Data.Save(run.Report,report);
  Set("workspace",run);Set("report",report);Set("reportPath",run.Report);Set("catalogPath",run.Catalog);Set("outputPath",folder);Set("appSettings",new AppSettings());
  C<TextBox>("LibraryPath").Text=root;C<TextBox>("SeriesName").Text="Show";Call("RefreshRunOptions");
  var initial=PreviewPlanner.Create(report,root,"Show",catalog,identity:run.SeriesIdentity);var target=initial.Items.Single().Destination!;
  Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.WriteAllText(target,"existing fixture");
  var plan=PreviewPlanner.Create(report,root,"Show",catalog,identity:run.SeriesIdentity);
  var preview=Path.Combine(folder,"move-preview");PreviewPlanner.Write(plan,preview);PreviewCache.Save(preview,(string)Call("CurrentPreviewSignature")!,plan);Call("DisplayPreview",plan);
  var grid=C<DataGrid>("PreviewGrid");grid.SelectedIndex=0;
  Check(C<Button>("KeepBothDestinationButton").IsEnabled&&C<Button>("SkipDestinationButton").IsEnabled&&!((PreviewRow)grid.SelectedItem).CanApply,"Occupied destinations offer Keep both and Leave in place before any move");
  void Click(string name) {
   var context=SynchronizationContext.Current;
   try {
    SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
    C<Button>(name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    var until=DateTime.UtcNow.AddSeconds(10);
    while(Get("operation")!=null&&DateTime.UtcNow<until){window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);Thread.Sleep(10);}
    if(Get("operation")!=null)throw new Exception("Destination choice did not complete.");
   }finally{SynchronizationContext.SetSynchronizationContext(context);}
  }
  Click("KeepBothDestinationButton");
  var chosen=(PreviewRow)grid.SelectedItem;
  Check(chosen.CanApply&&!chosen.Selected&&chosen.Destination.EndsWith(" (2).mkv")&&C<Button>("ResetDestinationButton").IsEnabled,"Keep both previews a new filename and still requires explicit move selection");
  Click("SkipDestinationButton");Check(!((PreviewRow)grid.SelectedItem).CanApply&&((PreviewRow)grid.SelectedItem).Item.DestinationChoice=="skip","Leave in place persists a skipped destination choice");
  Click("ResetDestinationButton");Check(((PreviewRow)grid.SelectedItem).Item.DestinationConflict,"Reset returns the file to destination-conflict review");
  using(var stop=new CancellationTokenSource()) {
   Set("operation",stop);Call("RefreshMoveSelection");Check(!C<Button>("KeepBothDestinationButton").IsEnabled&&!C<Button>("SkipDestinationButton").IsEnabled,"Destination choices are disabled during processing");
   Set("operation",null);Call("RefreshMoveSelection");
  }
  Check(File.ReadAllText(source)=="original fixture"&&File.ReadAllText(target)=="existing fixture","Destination review leaves both media fixtures unchanged");
 }
}
