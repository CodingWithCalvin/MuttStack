using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Scout.Core;
using Scout.Desktop;

static class RunOptionsDesktopTests {
 public static void Run(MainWindow window,WorkspaceStore store) {
  const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
  object? Call(string name,params object[] args)=>typeof(MainWindow).GetMethod(name,flags)!.Invoke(window,args);
  void Set(string name,object? value)=>typeof(MainWindow).GetField(name,flags)!.SetValue(window,value);
  T C<T>(string name)=>(T)window.FindName(name);
  void Check(bool yes,string message){if(!yes)throw new Exception(message);Console.WriteLine("PASS "+message);}
  var root=Path.Combine(store.Root,"options-test-source");Directory.CreateDirectory(root);
  var first=store.CreateRun(root);var second=store.CreateRun(root);
  Set("workspace",first);Set("outputPath",store.DirectoryFor(first));C<TextBox>("LibraryPath").Text=root;Call("RefreshRunOptions");
  Check(C<CheckBox>("IncludeBonusContent").IsChecked==false&&C<CheckBox>("IncludeCompilations").IsChecked==false&&C<ComboBox>("DestinationChoice").SelectedIndex==0,"New Scouts prioritize episodes with optional extras off");
  Check(!first.UseAiFallback&&C<CheckBox>("UseAiFallback").IsChecked==false,"New Scouts leave AI fallback off by default");
  C<CheckBox>("UseAiFallback").IsChecked=true;
  Check(store.ReadAll().Single(r=>r.Id==first.Id).UseAiFallback,"AI fallback selection persists for this Scout");
  C<CheckBox>("IncludeBonusContent").IsChecked=true;C<ComboBox>("DestinationChoice").SelectedIndex=1;
  var destination=Path.Combine(Path.GetDirectoryName(store.Root)!,"Library","Example Show");C<NamingTemplateEditor>("DestinationRootEntry").Text=DestinationTemplate.Literal(destination);
  Check((string?)Call("SelectedDestinationRoot")==destination&&store.ReadAll().Single(r=>r.Id==first.Id).DestinationTemplateOverride==DestinationTemplate.Literal(destination),"Separate destination and bonus options persist per Scout");
  Set("workspace",second);Call("RefreshRunOptions");Check(C<CheckBox>("IncludeBonusContent").IsChecked==false&&C<NamingTemplateEditor>("DestinationRootEntry").Text=="","Another Scout keeps independent content and destination settings");
  Check(C<CheckBox>("UseAiFallback").IsChecked==false,"Another Scout keeps AI fallback disabled independently");
  Set("workspace",store.ReadAll().Single(r=>r.Id==first.Id));Call("RefreshRunOptions");Check(C<ComboBox>("DestinationChoice").SelectedIndex==1&&C<NamingTemplateEditor>("DestinationRootEntry").Text==DestinationTemplate.Literal(destination),"Reopening a Scout restores its complete destination selection");
  Check(C<CheckBox>("UseAiFallback").IsChecked==true,"Reopening a Scout restores its AI fallback choice");
  var item=new PlanItem{Source=Path.Combine(root,"episode.mkv"),Destination=Path.Combine(destination,"Season 01","Example Show - S01E01 - Opening.mkv"),GroupId="test",Category="episode",Action="rename/move"};
  Call("DisplayPreview",new MovePlan(root,"Example Show",[item],[]){DestinationRoot=destination});
  var previewRow=C<DataGrid>("PreviewGrid").Items[0];
  Check(previewRow.GetType().GetProperties().Any(p=>p.PropertyType==typeof(string)&&(string?)p.GetValue(previewRow)==item.Destination),"Move preview displays the full destination path");
  C<CheckBox>("IncludeCompilations").IsChecked=true;Check(C<DataGrid>("PreviewGrid").Items.Count==0,"Changing content options clears the old move preview");
  var reportPath=Path.Combine(store.DirectoryFor(first),"report.json");
  Data.Save(reportPath,new JsonObject{["files"]=new JsonArray(new JsonObject{["path"]=Path.Combine(root,"remaining.mkv")},new JsonObject{["path"]=@"D:\isolated-preview-fixture\moved.mkv"})});
  Set("openingWorkspace",true);try{window.LoadReport(reportPath);}finally{Set("openingWorkspace",false);}
  Check(C<TextBox>("LibraryPath").Text==root&&C<TextBlock>("FileCount").Text=="2","Reopened results can span source and destination drives without changing the source root");
  using(var operation=new CancellationTokenSource()) {Set("operation",operation);Call("SetBusy",true);Check(!C<Border>("RunOptionsPanel").IsEnabled,"Content and destination cannot change during processing");Set("operation",null);Call("SetBusy",false);}
  Call("ShowPage","Library");
  if(Environment.GetEnvironmentVariable("SCOUT_TEST_RENDERS") is string render) {
   Directory.CreateDirectory(render);var panel=(FrameworkElement)window.Content;if(panel is Panel p)p.Background=window.Background;
   window.Width=1420;window.Height=920;window.UpdateLayout();
   C<Border>("RunOptionsPanel").BringIntoView();window.Dispatcher.Invoke(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
   var image=new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth),(int)Math.Ceiling(panel.ActualHeight),96,96,PixelFormats.Pbgra32);image.Render(panel);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(Path.Combine(render,"scout-run-options.png"));encoder.Save(file);
  }
 }
}
