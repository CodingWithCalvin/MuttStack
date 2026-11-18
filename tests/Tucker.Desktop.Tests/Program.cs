using System.IO;
using System.Reflection;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Tucker;
class Program
{
 [STAThread] static void Main(string[] args) {
  var app=new Tucker.App{ShutdownMode=ShutdownMode.OnExplicitShutdown};app.InitializeComponent();
  app.Dispatcher.InvokeAsync(async()=>{
   var root=Path.GetFullPath(args[1]);Directory.CreateDirectory(root);var dbPath=Path.Combine(root,"doggo.db");int passed=0;
   void Check(bool yes,string label){if(!yes)throw new Exception(label);Console.WriteLine("PASS "+label);passed++;}
   MainWindow? w=null;
   try {
    string Source(string label){var dir=Path.Combine(root,"raw",label);Directory.CreateDirectory(dir);File.WriteAllText(Path.Combine(dir,"title_t00.mkv"),"raw");return dir;}
    var source=Source("Show S1 D1");
    var json=Path.Combine(root,"presets.json");File.WriteAllText(json,"""{"VersionMajor":72,"PresetList":[{"PresetName":"UI custom","Type":1,"Folder":false,"FileFormat":"av_mkv","VideoEncoder":"x265_10bit","VideoQualitySlider":18,"VideoPreset":"slow"}]}""");
    var database=new DoggoDatabase(dbPath);database.Settings(new {Cli=Path.GetFullPath(args[0]),PresetFile=json,GuiSettings="",Output=Path.Combine(root,"out"),Format="{disc_label}",Prefix="",Preset="UI custom",Decoder=2,Series="Obsolete global series",Year="1999",Tvdb="999",FilenameFormat="{filename}"});
    w=new(root,dbPath);
    T Control<T>(string id)=>(T)w.FindName(id);
    void Click(string id)=>Control<Button>(id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    object? Call(string name,params object?[] values)=>typeof(MainWindow).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(w,values);
    ObservableCollection<string> Folders()=>(ObservableCollection<string>)typeof(MainWindow).GetField("folders",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(w)!;
    async Task Preview()=>await (Task)Call("PreparePreview")!;
    void SelectInbox(params string[] ids){Click("NavInbox");var grid=Control<DataGrid>("InboxGrid");grid.SelectedItems.Clear();foreach(var row in grid.Items.Cast<InboxItem>().Where(i=>ids.Contains(i.Id)))grid.SelectedItems.Add(row);}
    Handoff Offer(string label){var dir=Source(label);var path=Path.Combine(dir,"title_t00.mkv");var stat=new FileInfo(path);var manifest=new HandoffManifest{Id=Guid.NewGuid().ToString("D"),Files=[new(){Source=path,SourceSize=stat.Length,SourceModified=stat.LastWriteTimeUtc,Series=new("tvdb",label,label,"2020",""),Season="2",DiscLabel=label,DiscId=label,RipDate=DateTime.Now}]};var file=Path.Combine(root,manifest.Id+".json");QueueStore.Write(file,manifest);var h=Handoff.Read(file);new Doggo.DoggoInbox(dbPath).Offer(h.Manifest.Id,h.Digest,System.Text.Json.JsonSerializer.Serialize(h.Manifest));return h;}
    Check(Control<FrameworkElement>("NewTuckerPage").Visibility==Visibility.Visible&&!Control<Button>("StartAll").IsEnabled&&!Control<Button>("AddWaiting").IsEnabled&&w.FindName("NavQueue")==null&&w.FindName("NavSources")==null,"Empty startup opens New Tucker without redundant queue or source navigation");
    Check(Control<ComboBox>("PresetChoice").SelectedItem is HandBrakePreset {Name:"UI custom"}&&Control<TextBlock>("PresetSummary").Text.Contains("slow"),"Saved GUI preset and encoder summary loaded");
    Folders().Add(source);await Preview();
    Check(Control<DataGrid>("PreviewGrid").Items.Count==1&&Control<Button>("AddWaiting").IsEnabled&&!Control<Button>("AddRunning").IsEnabled,"Existing rips can be previewed without starting processing");
    Control<TextBox>("OutputFormat").Text="{series}";await Preview();
    Check(Control<DataGrid>("PreviewGrid").Items.Count==0&&Control<TextBlock>("Status").Text.Contains("{series}"),"Obsolete global series settings cannot fill missing per-file metadata");
    Control<TextBox>("OutputFormat").Text="{disc_label}";await Preview();Control<DataGrid>("PreviewGrid").SelectedIndex=0;
    Check(Control<TextBox>("PreviewPaths").Text.Contains(source)&&Control<TextBox>("PreviewPaths").Text.Contains(Path.Combine(root,"out")),"Preview exposes complete input and output paths");
    var firstName=Control<TextBlock>("Heading").Text;Click("AddWaiting");
    Check(w.Manager.State.Jobs.Count==1&&!w.Manager.Running&&Control<ListBox>("QueueList").Items.Count==1&&Control<TextBlock>("Heading").Text==firstName&&Control<DataGrid>("QueueGrid").Items.Count==1,"Create Tucker saves and opens one named run without encoding");
    Control<TextBox>("Destination").Text=Path.Combine(root,"different");Call("SaveSettings",w,new RoutedEventArgs());
    var stored=w.Manager.Database.Settings<System.Text.Json.Nodes.JsonObject>()!;
    Check(!stored.ContainsKey("Series")&&!stored.ContainsKey("Year")&&!stored.ContainsKey("Tvdb")&&w.Manager.State.Jobs[0].Encoding!.Output.StartsWith(Path.Combine(root,"out")),"Saving preferences drops obsolete metadata and preserves frozen run settings");
    Control<DataGrid>("QueueGrid").SelectedIndex=0;Click("StartSelected");await w.Manager.RunTask!;
    Check(w.Manager.State.Jobs[0].Status=="Completed"&&File.ReadAllText(Path.Combine(source,"title_t00.mkv"))=="raw"&&Control<TextBox>("Paths").Text.Contains(w.Manager.State.Jobs[0].OutputPath),"A saved Tucker completes with selectable final paths and original media retained");
    w.Close();w=new(root,dbPath);
    Check(!w.Manager.Running&&Control<ListBox>("QueueList").Items.Cast<QueueTile>().Single().Summary.StartsWith("Completed")&&Control<TextBlock>("Heading").Text==firstName,"History restores completed runs and opens a saved run without auto-start");
    Check(Control<Border>("InboxBadge").Visibility==Visibility.Collapsed,"An empty inbox has no badge");
    var first=Offer("Show S2 D1");var second=Offer("Other S2 D2");await Until(()=>Control<TextBlock>("InboxCount").Text=="2");
    Check(Control<FrameworkElement>("QueuePage").Visibility==Visibility.Visible&&Control<Border>("InboxBadge").Visibility==Visibility.Visible,"Inbox arrivals update the badge while viewing history");
    Click("NewTuckerButton");var extra=Source("External S3 D1");var extra2=Source("External S4 D1");Folders().Add(extra);Folders().Add(extra2);Folders().Add(source);Folders().Add(Path.GetDirectoryName(first.Manifest.Files[0].Source)!);
    SelectInbox(first.Manifest.Id,second.Manifest.Id);Call("RefreshInbox");
    Check(Control<DataGrid>("InboxGrid").SelectedItems.Count==2,"Inbox refresh preserves multiple selected batches");
    Click("UseInboxBatches");
    Check(Control<ListBox>("SourcesList").Items.Count==6&&Control<FrameworkElement>("NewTuckerPage").Visibility==Visibility.Visible&&database.PendingInboxCount()==2,"Multiple inbox batches join multiple existing folders without acceptance");
    await Preview();var planned=Control<DataGrid>("PreviewGrid").Items.Cast<Job>().ToList();
    Check(planned.Count==5&&planned.Count(j=>j.Status=="Already imported")==1&&planned.Count(j=>j.Series!=null)==2,"Combined preview deduplicates overlaps, preserves metadata and identifies prior imports");
    var previewPath=planned.Single(j=>j.Source==first.Manifest.Files[0].Source).OutputPath;
    var combinedName=Control<TextBlock>("Heading").Text;
    if(args.Length>2){Directory.CreateDirectory(args[2]);await Render(w,1440,920,Path.Combine(args[2],"combined-preview.png"));await Render(w,1080,740,Path.Combine(args[2],"combined-preview-minimum.png"));Check(Control<DataGrid>("PreviewGrid").ActualHeight>=132,"Combined preview keeps room for multiple files at minimum window size");}
    Click("AddWaiting");
    var combined=w.Manager.State.Jobs.Where(j=>j.QueueName==combinedName).ToList();
    Check(combined.Count==4&&combined.Select(j=>j.QueueId).Distinct().Count()==1&&Control<ListBox>("QueueList").Items.Count==2&&!w.Manager.Running&&combined.Single(j=>j.Source==first.Manifest.Files[0].Source).OutputPath==previewPath,"The mixed selection becomes one four-file run, opens in history and preserves preview paths");
    Check(database.Inbox().All(i=>i.Status=="Accepted")&&w.Manager.State.Receipts.Count==2&&Control<Border>("InboxBadge").Visibility==Visibility.Collapsed,"All selected inbox batches are acknowledged together and the badge clears immediately");
    Click("NewTuckerButton");var later=Source("Later S5 D1");Folders().Add(later);await Preview();Folders().Add(extra);
    Check(!Control<Button>("AddWaiting").IsEnabled&&Control<DataGrid>("PreviewGrid").Items.Count==0,"Changing the source list invalidates a stale preview");
    Control<ListBox>("SourcesList").SelectedItem=Control<ListBox>("SourcesList").Items.Cast<DraftSource>().Single(d=>d.Id=="folder:"+extra);Click("RemoveSourcesButton");await Preview();var laterName=Control<TextBlock>("Heading").Text;Click("AddWaiting");
    Click("NavSettings");var history=Control<ListBox>("QueueList");history.SelectedItem=history.Items.Cast<QueueTile>().Single(q=>q.Name==combinedName);
    Check(Control<DataGrid>("QueueGrid").Items.Count==4&&Control<TextBlock>("Heading").Text==combinedName&&Control<Button>("StartSelected").IsEnabled,"History selection opens only that run's files and controls from another screen");
    Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE","encode-delay");Click("StartSelected");await Until(()=>w.Manager.Running&&w.Manager.Current!=null);
    Click("NewTuckerButton");
    Check(Control<Border>("ActivityFooter").Visibility==Visibility.Visible&&Control<FrameworkElement>("ActiveEncoding").Visibility==Visibility.Visible&&Control<Button>("PauseButton").IsEnabled&&!Control<Button>("StartAll").IsEnabled,"Processing and pause controls stay available in New Tucker's activity footer");
    if(args.Length>2)await Render(w,1080,740,Path.Combine(args[2],"active-footer-minimum.png"));
    await w.Manager.RunTask!;Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE",null);
    Check(combined.All(j=>j.Status=="Completed")&&w.Manager.State.Jobs.Single(j=>j.QueueName==laterName).Status=="Waiting","Start this Tucker processes only the selected combined run");
    Click("NavSettings");Click("StartAll");await w.Manager.RunTask!;
    Check(w.Manager.State.Jobs.All(j=>j.Status=="Completed")&&Control<ProgressBar>("OverallProgress").Value==100,"Start all works from Settings and the footer reports overall completion");
    var pending=Offer("Next S6 D1");Call("RefreshInboxStatus");history=Control<ListBox>("QueueList");history.SelectedItem=history.Items.Cast<QueueTile>().Single(q=>q.Name==combinedName);
    if(args.Length>2){await Render(w,1440,920,Path.Combine(args[2],"tucker-history.png"));await Render(w,1080,740,Path.Combine(args[2],"tucker-history-minimum.png"));}
    SelectInbox(pending.Manifest.Id);Click("UseInboxBatches");Click("NewTuckerButton");
    Check(Control<ListBox>("SourcesList").Items.Count==0&&!Control<Button>("AddWaiting").IsEnabled&&database.PendingInboxCount()==1,"Starting a fresh draft does not accept or lose staged inbox batches");
    SelectInbox(pending.Manifest.Id);Call("DeclineHandoff",w,new RoutedEventArgs());Check(Control<Border>("InboxBadge").Visibility==Visibility.Collapsed,"Declined batches are excluded from the waiting indicator");
    w.Close();Offer("New arrival S7 D1");w=new(root,dbPath);
    Check(Control<TextBlock>("InboxCount").Text=="1"&&Control<ListBox>("QueueList").Items.Count==3&&w.Manager.State.Jobs.Count==6&&w.Manager.State.Receipts.Count==2&&!w.Manager.Running,"Reopening restores all run memberships, receipts and waiting inbox indicator without auto-start");w.Close();
    passed+=await AppendDesktopTests.Run(root,Path.GetFullPath(args[0]),args.Length>2?args[2]:null);
    passed+=await HistoryDesktopTests.Run(root,Path.GetFullPath(args[0]),args.Length>2?args[2]:null);
    passed+=await FolderPreviewDesktopTests.Run(root,Path.GetFullPath(args[0]),args.Length>2?args[2]:null);
    passed+=await ScoutDesktopTests.Run(root,args.Length>2?args[2]:null);
    Console.WriteLine($"{passed} desktop checks passed");app.Shutdown(0);Dispatcher.CurrentDispatcher.InvokeShutdown();
   }catch(Exception ex){Console.Error.WriteLine(ex);if(w?.Manager.Running==true){w.Manager.Stop();await w.Manager.RunTask!;}w?.Close();Environment.ExitCode=1;app.Shutdown(1);Dispatcher.CurrentDispatcher.InvokeShutdown();}
   finally{Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE",null);}
  });Dispatcher.Run();
 }
 static async Task Until(Func<bool> condition){var timeout=DateTime.UtcNow.AddSeconds(8);while(!condition()){if(DateTime.UtcNow>timeout)throw new TimeoutException("Desktop operation did not finish.");await Task.Delay(25);}}
 static async Task Render(MainWindow window,int width,int height,string path){window.ApplyTemplate();var root=(FrameworkElement)window.Content;root.Measure(new Size(width,height));root.Arrange(new Rect(0,0,width,height));root.UpdateLayout();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);root.UpdateLayout();var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);}
}
