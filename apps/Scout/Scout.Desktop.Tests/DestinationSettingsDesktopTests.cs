using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Scout.Core;
using Scout.Desktop;

static class DestinationSettingsDesktopTests {
 public static void Run(MainWindow window,WorkspaceStore store) {
  const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
  object? Call(string name,params object[] args)=>typeof(MainWindow).GetMethod(name,flags)!.Invoke(window,args);
  void Set(string name,object? value)=>typeof(MainWindow).GetField(name,flags)!.SetValue(window,value);
  T C<T>(string name)=>(T)window.FindName(name);
  void Check(bool yes,string message){if(!yes)throw new Exception(message);Console.WriteLine("PASS "+message);}
  var root=Path.Combine(store.Root,"global-destination-source");Directory.CreateDirectory(root);
  var run=store.CreateRun(root);run.SeriesIdentity=new("tvdb","82493","iCarly","2007");run.Name="iCarly";store.Save(run);
  Set("workspace",run);Set("report",null);Set("reportPath",null);Set("catalogPath","");Set("outputPath",store.DirectoryFor(run));
  C<TextBox>("LibraryPath").Text=root;C<TextBox>("SeriesName").Text="iCarly";Call("RefreshHistory");
  var previousTemplate=C<NamingTemplateEditor>("DefaultPathTemplate").Text;
  Set("workspace",null);C<NamingTemplateEditor>("DefaultPathTemplate").Text=@"Season {season:00}\{series} ({year}) - {code} - {title}";
  Check(C<TextBlock>("DefaultNamingExample").Text.Contains("Example Show (2024) - S01E01"),"Relative naming preview supplies an illustrative release year without a selected Scout");
  Set("workspace",run);Call("NamingChanged",window,new RoutedEventArgs());
  Check(C<TextBlock>("DefaultNamingExample").Text.Contains("iCarly (2007) - S01E01"),"Relative naming preview uses the selected show's release year");
  var identity=run.SeriesIdentity;run.SeriesIdentity=identity with{Year=""};Call("NamingChanged",window,new RoutedEventArgs());
  Check(C<TextBlock>("DefaultNamingExample").Text.Contains("release year")&&!C<TextBlock>("DefaultNamingExample").Text.Contains("2024"),"Missing real release years are reported instead of replaced with example metadata");
  run.SeriesIdentity=identity;C<NamingTemplateEditor>("DefaultPathTemplate").Text=previousTemplate;
  C<NamingTemplateEditor>("DefaultDestinationTemplate").Text=@"E:\TV\{series} ({year}) {{{provider}-{id}}}";
  Check(Call("SelectedDestinationRoot")==null&&C<TextBlock>("DefaultDestinationExample").Text.Contains(@"E:\TV\iCarly (2007) {tvdb-82493}\Season 01\"),"Global template edits show complete sample paths before affecting saved destinations");
  Call("SaveSettingsCore");
  var preferences=(SettingsStore)typeof(MainWindow).GetField("settingsStore",flags)!.GetValue(window)!;
  Check((string?)Call("SelectedDestinationRoot")==@"E:\TV\iCarly (2007) {tvdb-82493}"&&preferences.Load().DestinationTemplate.Contains("{year}")&&!C<StackPanel>("DestinationRootPanel").IsEnabled&&C<TextBlock>("RunDestinationExample").Text.Contains("Using global default"),"Saving global destination updates inheriting Scouts and persists the token template");
  Render("Settings","scout-global-destination.png");
  C<ComboBox>("DestinationChoice").SelectedIndex=1;C<NamingTemplateEditor>("DestinationRootEntry").Text=@"F:\Archive\{series}";
  C<NamingTemplateEditor>("DefaultDestinationTemplate").Text=@"G:\Shows\{series}";Call("SaveSettingsCore");
  Check((string?)Call("SelectedDestinationRoot")==@"F:\Archive\iCarly"&&store.ReadAll().Single(r=>r.Id==run.Id).DestinationTemplateOverride==@"F:\Archive\{series}","Per-Scout token overrides survive global changes and storage roundtrip");
  Render("Library","scout-destination-override.png");
  C<ComboBox>("DestinationChoice").SelectedIndex=2;Call("SaveSettingsCore");
  Check(Call("SelectedDestinationRoot")==null&&C<TextBlock>("RunDestinationExample").Text.Contains("Organize within the source folder"),"A Scout can opt out of the global move destination");
  C<ComboBox>("DestinationChoice").SelectedIndex=0;Check((string?)Call("SelectedDestinationRoot")==@"G:\Shows\iCarly","Returning to global default uses the latest saved destination");
  C<ComboBox>("DestinationChoice").SelectedIndex=1;C<NamingTemplateEditor>("DestinationRootEntry").Text="";Call("RefreshRunOptions");
  Check(C<ComboBox>("DestinationChoice").SelectedIndex==1&&C<TextBlock>("RunDestinationExample").Text.Contains("Enter a destination template"),"An empty custom destination remains an explicit validation error");
  C<NamingTemplateEditor>("DefaultDestinationTemplate").Text=@"E:\{season:00}";bool rejected=false;
  try{Call("SaveSettingsCore");}catch(TargetInvocationException ex) when(ex.InnerException is ArgumentException){rejected=true;}
  Check(rejected&&preferences.Load().DestinationTemplate==@"G:\Shows\{series}","Invalid global tokens cannot replace previously saved destination settings");
  Call("LoadSettings");
  run.DestinationMode="";run.DestinationTemplateOverride=null;run.DestinationRoot=@"F:\My  Library\iCarly {tvdb-82493}";Call("RefreshRunOptions");
  C<CheckBox>("IncludeBonusContent").IsChecked=!C<CheckBox>("IncludeBonusContent").IsChecked;
  Check((string?)Call("SelectedDestinationRoot")==@"F:\My  Library\iCarly {tvdb-82493}"&&run.DestinationMode=="custom","Editing an older Scout preserves its literal destination as an override");
  foreach(var mode in new[]{"source","custom"}) {
   Set("workspace",run);run.DestinationMode=mode;run.DestinationRoot="";run.DestinationTemplateOverride=mode=="custom"?@"F:\Old\{series}":null;store.Save(run);Call("RefreshRunOptions");
   Call("CreateRun",root);var created=(RunWorkspace)typeof(MainWindow).GetField("workspace",flags)!.GetValue(window)!;
   Check(created.DestinationMode=="default"&&created.DestinationRoot==""&&created.DestinationTemplateOverride==null&&C<ComboBox>("DestinationChoice").SelectedIndex==0&&store.ReadAll().Single(r=>r.Id==run.Id).DestinationMode==mode,
    "New Scout uses global default when the previous Scout chose "+mode);
  }
  foreach(var oldDestination in new[]{root,@"F:\Previous Destination"}) {
   var import=Path.Combine(store.Root,"import-fixture-"+Guid.NewGuid().ToString("N"),"report.json");
   var imported=new JsonObject{["source_root"]=root,["destination_root"]=oldDestination,["series_identity"]=JsonSerializer.SerializeToNode(run.SeriesIdentity,Data.Options),
    ["files"]=new JsonArray(new JsonObject{["path"]=Path.Combine(root,"episode.mkv")})};
   Data.Save(import,imported);window.LoadReport(import);
   var created=(RunWorkspace)typeof(MainWindow).GetField("workspace",flags)!.GetValue(window)!;
   Check(created.DestinationMode=="default"&&C<ComboBox>("DestinationChoice").SelectedIndex==0&&(string?)Call("SelectedDestinationRoot")==@"G:\Shows\iCarly"&&Data.Read(created.Report).Text("destination_root")==oldDestination,
    "Imported results start with global default and retain move history for "+(oldDestination==root?"source-folder organization":"an earlier destination"));
  }
  void Render(string page,string name) {
   Call("ShowPage",page);
   if(Environment.GetEnvironmentVariable("SCOUT_TEST_RENDERS") is not string render)return;
   Directory.CreateDirectory(render);window.Width=1420;window.Height=1100;window.UpdateLayout();
   if(page=="Settings"){C<TabControl>("SettingsTabs").SelectedIndex=0;C<NamingTemplateEditor>("DefaultDestinationTemplate").BringIntoView();}
   else C<Border>("RunOptionsPanel").BringIntoView();
   window.Dispatcher.Invoke(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
   var panel=(FrameworkElement)window.Content;if(panel is Panel p)p.Background=window.Background;
   var image=new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth),(int)Math.Ceiling(panel.ActualHeight),96,96,PixelFormats.Pbgra32);image.Render(panel);
   var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(Path.Combine(render,name));encoder.Save(file);
  }
 }
}
