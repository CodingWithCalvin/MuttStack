using Ripley;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;

partial class Program
{
    static int passed;
    static void Check(bool condition, string name) { if(!condition) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
    static readonly BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static object? Call(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
    static object? Field(MainWindow window, string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window);
    static void Set(MainWindow window, string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
    static T Control<T>(MainWindow window, string name) where T : FrameworkElement => (T)window.FindName(name);
    static async Task Until(Func<bool> condition) { var limit = DateTime.UtcNow.AddSeconds(10); while(!condition()) { if(DateTime.UtcNow > limit) throw new TimeoutException("Desktop operation did not finish."); await Task.Delay(25); } }

    [STAThread] static int Main(string[] args)
    {
        var temp = Path.Combine(Path.GetTempPath(), "RipleyDesktopTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try {
            var app = new App(); app.InitializeComponent();
            var state=Path.Combine(temp,"state");Directory.CreateDirectory(state);
            File.WriteAllText(Path.Combine(state,"settings.json"), """{"Cli":"legacy-handbrake.exe","CompressionPrefix":"OLD_"}""");
            File.WriteAllText(Path.Combine(state,"queue.json"), "[ ]");
            var window = new MainWindow(state);
            var task = window.Dispatcher.InvokeAsync(() => Run(window, temp, Path.GetFullPath(args[0]), args.Length > 1 ? args[1] : null)).Task.Unwrap();
            var frame = new DispatcherFrame();
            _ = task.ContinueWith(_ => window.Dispatcher.BeginInvoke(() => frame.Continue = false));
            Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
            Console.WriteLine($"{passed} desktop checks passed"); return 0;
        }
        catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE", null); Directory.Delete(temp, true); }
    }

    static async Task Run(MainWindow window,string temp,string fake,string? renders)
    {
        await CheckDriveNotifications(temp);
        Check(window.Title=="Ripley"&&window.WindowStyle==WindowStyle.None&&WindowChrome.GetWindowChrome(window).CaptionHeight==38,"Ripley keeps Scout's custom shell");
        foreach(var page in new[]{"Rip","Settings"}) {
            Call(window,"ShowPage",page);
            Check(Control<FrameworkElement>(window,page+"Page").Visibility==Visibility.Visible&&Control<FrameworkElement>(window,(page=="Rip"?"Settings":"Rip")+"Page").Visibility==Visibility.Collapsed,"Navigation selects only "+page);
        }
        Check(new[]{"SideCompress","CompressPage","QueueRipButton","Cli","PresetFile","Hardware","Destination","ResumeButton","StopAllButton"}.All(n=>window.FindName(n)==null),"Ripley has no compression navigation, queue, preset, output or processing controls");
        Text(window,"DiscLabel").Text="ICARLY_S1_V2_D2";
        Check(Text(window,"Season").Text=="1"&&Text(window,"Volume").Text=="2"&&Text(window,"DiscNumber").Text=="2","Disc label still fills season, volume and disc");
        Text(window,"DiscLabel").Text="UNKNOWN";
        Check(Text(window,"Season").Text==""&&Text(window,"Volume").Text==""&&Text(window,"DiscNumber").Text=="","A new unknown disc clears stale values");
        Configure(window,temp,fake);CheckHistory(window,temp);
        await CheckRip(window,temp);
        await CheckLockedHistory(temp);
        await CheckTuckerInbox(temp,fake,renders);
        await CheckHistoryActions(temp,renders);
        await CheckCancellation(window,temp);
        Call(window,"SavePreferences",window,new RoutedEventArgs());
        var saved=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(temp,"state","settings.json")))!;
        Check(saved["FilenamePrefix"]!.ToString()=="{volume_number}_{disc_number}_"&&saved["Cli"]!.ToString()=="legacy-handbrake.exe"&&saved["CompressionPrefix"]!.ToString()=="OLD_","Saving ripping preferences preserves dormant legacy data without applying it");
        Check(File.ReadAllText(Path.Combine(temp,"state","queue.json"))=="[ ]","Rip, cancel, handoff and settings do not rewrite the legacy queue");
        var restored=new MainWindow(Path.Combine(temp,"state"));
        var history=(ObservableCollection<RipSession>)Field(restored,"ripHistory")!;
        var completed=history.First(h=>h.Status=="Completed"&&!h.IsLocked);Control<ListBox>(restored,"HistoryList").SelectedItem=completed;
        Check(((List<Job>)Field(restored,"lastRipFiles")!).Select(j=>j.Source).SequenceEqual(completed.Files.Select(j=>j.Source))&&!Control<Button>(restored,"RipButton").IsEnabled&&Control<Button>(restored,"SendTuckerButton").IsEnabled,"Completed history restores exact raw files for sending without reripping");
        if(renders!=null) {
            Directory.CreateDirectory(renders);
            await Render(restored,1440,920,Path.Combine(renders,"rip.png"));await Render(restored,1080,740,Path.Combine(renders,"rip-minimum.png"));
            Control<ScrollViewer>(restored,"RipSetupScroll").ScrollToBottom();await Render(restored,1080,740,Path.Combine(renders,"rip-folders.png"));
            Call(restored,"ShowPage","Settings");await Render(restored,1080,740,Path.Combine(renders,"settings-minimum.png"));
        }
        completed.Status="Ripping";Call(restored,"SaveHistory");restored.Close();
        var recovered=new MainWindow(Path.Combine(temp,"state"));
        Check(((ObservableCollection<RipSession>)Field(recovered,"ripHistory")!).Single(h=>h.Id==completed.Id).Status=="Interrupted"&&!(bool)Field(recovered,"discBusy")!,"Interrupted history is recovered without restarting a physical disc operation");
        recovered.Close();window.Close();
    }
    static TextBox Text(MainWindow w,string name)=>Control<TextBox>(w,name);
    static async Task CheckDriveNotifications(string temp)
    {
        var drives=new List<OpticalDrive>{new(0,"DVD","ICARLY_S1_V2_D2","E:"),new(1,"DVD","","F:")};
        var reads=0;
        var w=new MainWindow(Path.Combine(temp,"drive-state"),()=>{Interlocked.Increment(ref reads);return drives.ToList();});
        Call(w,"NewRipley",w,new RoutedEventArgs());
        await (Task)Call(w,"RefreshDriveList")!;
        Check(Control<ComboBox>(w,"DriveChoice").Items.Count==2&&Text(w,"DiscLabel").Text=="ICARLY_S1_V2_D2"&&Text(w,"Volume").Text=="2","Windows drive discovery lists empty drives and fills the loaded disc without launching MakeMKV");
        Text(w,"Volume").Text="7";var inspected=new DiscInfo();Set(w,"inspected",inspected);
        await (Task)Call(w,"RefreshDriveList")!;
        Check(Text(w,"Volume").Text=="7"&&ReferenceEquals(Field(w,"inspected"),inspected),"Refreshing unchanged media preserves manual disc values and inspection");
        Call(w,"SavePreferences",w,new RoutedEventArgs());
        var restored=new MainWindow(Path.Combine(temp,"drive-state"),()=>drives.ToList());await (Task)Call(restored,"RefreshDriveList")!;
        Check(Text(restored,"Volume").Text=="7","Startup detection of the same disc preserves a saved draft's manual values");
        NotifyDrive(w,0x8000,1u<<5);
        drives[1]=drives[1] with {Label="OTHER_S3_D1"};await (Task)Call(w,"RefreshDriveList")!;
        Check(((OpticalDrive)Control<ComboBox>(w,"DriveChoice").SelectedItem).Device=="E:"&&Text(w,"Volume").Text=="7","Media in another drive does not replace the selected disc or its manual values");
        NotifyDrive(w,0x8004,1u<<4);drives[0]=drives[0] with {Label=""};await (Task)Call(w,"RefreshDriveList")!;
        Check(Control<ComboBox>(w,"DriveChoice").Items.Count==2&&Text(w,"DiscLabel").Text==""&&Text(w,"Season").Text==""&&Field(w,"inspected")==null,"Media removal retains the physical drive and clears stale draft metadata");
        NotifyDrive(w,0x8000,1u<<4);drives[0]=drives[0] with {Label="ICARLY_S2_V1_D1"};await (Task)Call(w,"RefreshDriveList")!;
        Check(Text(w,"Season").Text=="2"&&Text(w,"DiscNumber").Text=="1"&&!((bool)Field(w,"discBusy")!),"Insertion updates detected season and disc without starting ripping or compression");
        Call(w,"SetDiscBusy",true);var before=reads;
        NotifyDrive(w,0x8000,1u<<4);await (Task)Call(w,"RefreshDriveList")!;
        Check(reads==before&&(bool)Field(w,"driveRefreshPending")!&&Text(w,"Season").Text=="2","A media event during an active disc operation waits without scanning or changing its settings");
        Call(w,"SetDiscBusy",false);drives[0]=drives[0] with {Label="ICARLY_S3_V1_D1"};await (Task)Call(w,"RefreshDriveList")!;
        Check(reads==before+1&&Text(w,"Season").Text=="3","Deferred media refresh runs after the disc operation finishes");
        Call(w,"SetRipSessionStatus","Completed");var session=(RipSession)Field(w,"currentRipSession")!;
        NotifyDrive(w,0x8004,1u<<4);drives[0]=drives[0] with {Label=""};await (Task)Call(w,"RefreshDriveList")!;
        Check(session.DiscLabel=="ICARLY_S3_V1_D1"&&Text(w,"Season").Text=="3"&&((OpticalDrive)Control<ComboBox>(w,"DriveChoice").SelectedItem).Label=="","Disc changes refresh the physical drive while preserving completed history");
        NotifyDrive(w,0x8000,1u<<4,0);
        Check(!(bool)Field(w,"driveRefreshPending")!,"Non-media volume notifications are ignored");
        Call(w,"DriveNotificationHook",IntPtr.Zero,0x219,new IntPtr(7),IntPtr.Zero,false);
        drives.Clear();await (Task)Call(w,"RefreshDriveList")!;
        Check(Control<ComboBox>(w,"DriveChoice").Items.Count==0&&session.DiscLabel=="ICARLY_S3_V1_D1","Device disconnect notifications remove stale drives without changing saved history");
        var snapshot=new TaskCompletionSource<List<OpticalDrive>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var racing=new MainWindow(Path.Combine(temp,"drive-race"),()=>snapshot.Task.GetAwaiter().GetResult());
        var scanning=(Task)Call(racing,"RefreshDriveList")!;
        NotifyDrive(racing,0x8000,1u<<4);
        snapshot.SetResult([new(0,"DVD","STALE_S1_D1","E:")]);await scanning;
        Check(Text(racing,"DiscLabel").Text==""&&(bool)Field(racing,"driveRefreshPending")!,"A new media event discards an older in-flight drive scan");
        snapshot=new(TaskCreationOptions.RunContinuationsAsynchronously);snapshot.SetResult([new(0,"DVD","CURRENT_S2_D1","E:")]);
        await (Task)Call(racing,"RefreshDriveList")!;
        Check(Text(racing,"DiscLabel").Text=="CURRENT_S2_D1"&&!(bool)Field(racing,"driveRefreshPending")!,"The next settled scan applies the latest disc metadata");
    }
    static void NotifyDrive(MainWindow w,int change,uint mask,short flags=1)
    {
        var data=Marshal.AllocHGlobal(20);
        try {
            Marshal.WriteInt32(data,20);Marshal.WriteInt32(data,4,2);Marshal.WriteInt32(data,8,0);Marshal.WriteInt32(data,12,unchecked((int)mask));Marshal.WriteInt16(data,16,flags);
            Call(w,"DriveNotificationHook",IntPtr.Zero,0x219,new IntPtr(change),data,false);
        }
        finally {Marshal.FreeHGlobal(data);}
    }
    static void CheckHistory(MainWindow w,string temp)
    {
        var history=(ObservableCollection<RipSession>)Field(w,"ripHistory")!;
        Control<Button>(w,"NewRipleyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var first=history[0];
        Check(history.Count==1&&first.Title=="New Ripley"&&Text(w,"DiscLabel").Text==""&&Text(w,"Season").Text==""&&Text(w,"RawFolder").Text==Path.Combine(temp,"raw"),"New Ripley creates a fresh history item and clears disc values while retaining folder preferences");
        Text(w,"DiscLabel").Text="ICARLY_S1_V2_D2";Text(w,"DiscNumber").Text="7";
        Control<Button>(w,"NewRipleyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var second=history[0];Text(w,"DiscLabel").Text="ICARLY_S2_V3_D4";
        Control<ListBox>(w,"HistoryList").SelectedItem=first;
        Check(history.Count==2&&first.Id!=second.Id&&Text(w,"Season").Text=="1"&&Text(w,"DiscNumber").Text=="7","Switching history restores independent disc settings and manual values");
        Control<ListBox>(w,"HistoryList").SelectedItem=second;
        Check(Text(w,"Season").Text=="2"&&Text(w,"Volume").Text=="3"&&Text(w,"DiscNumber").Text=="4","A second history item retains its own detected disc values");
        Call(w,"SavePreferences",w,new RoutedEventArgs());var restored=new MainWindow(Path.Combine(temp,"state"));
        Check(((ObservableCollection<RipSession>)Field(restored,"ripHistory")!).Count==2&&((RipSession)Field(restored,"currentRipSession")!).Id==second.Id&&Text(restored,"Season").Text=="2","History and its selected draft survive reopening");
        Control<ListBox>(w,"HistoryList").SelectedItem=first;Text(w,"DiscNumber").Text="2";
        Call(w,"ShowPage","Settings");Call(w,"OpenRipHistory",new Button{DataContext=first},new RoutedEventArgs());
        Check(Control<FrameworkElement>(w,"RipPage").Visibility==Visibility.Visible&&history.Count==2,"Reopening the selected history item returns from settings without creating a new session");
        Check(w.FindName("NavRip")==null&&w.FindName("NavCompress")==null&&w.FindName("SideCompress")==null,"History and Settings remain the only sidebar destinations");
    }
    static void Configure(MainWindow w,string temp,string fake)
    {
        Text(w,"RawFolder").Text=Path.Combine(temp,"raw");Text(w,"RawTemplate").Text=@"{series}\Season {season:00}";
        Text(w,"FilenamePrefix").Text="{volume_number}_{disc_number}_";Text(w,"MakeMkv").Text=fake;
        Set(w,"series",new SeriesIdentity("tvdb","82493","iCarly","2007",""));Call(w,"DisplaySeries");
        var drive=Control<ComboBox>(w,"DriveChoice");drive.ItemsSource=new[]{new OpticalDrive(0,"DVD","ICARLY_S1_V2_D2","E:")};drive.SelectedIndex=0;drive.ApplyTemplate();
        Check(drive.Template.FindName("PART_Popup",drive)!=null,"Drive dropdown keeps its dark popup");
    }
    static async Task CheckRip(MainWindow w,string temp)
    {
        await (Task)Call(w,"RipDiscCore")!;
        var ripped=(List<Job>)Field(w,"lastRipFiles")!;
        Check(ripped.Count==1&&File.Exists(ripped[0].Source),"Rip saves raw files without HandBrake or compression configuration");
        Check(Control<Button>(w,"SendTuckerButton").Visibility==Visibility.Visible&&Control<Button>(w,"SendTuckerButton").IsEnabled,"Completed rips offer Send to Tucker");
        using(var sql=new Doggo.Sqlite(Path.Combine(temp,"state","doggo.db")))Check(sql.Query("SELECT id FROM handoff_batches").Count==0,"Ripping never sends a batch before the user chooses Send to Tucker");
        var first=ripped[0].Source;
        await (Task)Call(w,"RipDiscCore")!;
        Check(File.Exists(first)&&((List<Job>)Field(w,"lastRipFiles")!)[0].Source!=first,"Repeated raw filenames remain collision safe");
        var progress=(RipQueue)Field(w,"ripQueue")!;Check(progress.Files.Single().Status=="Saved","Ripping queue retains confirmed filenames and completion");
        Call(w,"NewRipley",w,new RoutedEventArgs());Text(w,"DiscLabel").Text="ICARLY_S1_V2_D2";
        Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE","partial");await (Task)Call(w,"RipDiscCore")!;Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE",null);
        Check(((List<Job>)Field(w,"lastRipFiles")!).Count==0&&Directory.GetFiles(Text(w,"RawFolder").Text,"title_t00.mkv",SearchOption.AllDirectories).Length==1&&!Control<Button>(w,"SendTuckerButton").IsEnabled,"An incomplete rip keeps raw data and cannot be handed off");
    }
    static async Task CheckLockedHistory(string temp)
    {
        var state=Path.Combine(temp,"legacy-lock-state");Directory.CreateDirectory(state);
        var files=MakeDisc(Path.Combine(temp,"legacy-raw"),"LEGACY_S1_D1","iCarly","82493","1",1);
        var run=new RipSession{Status="Completed",DiscLabel="LEGACY_S1_D1",Files=files};
        QueueStore.Write(Path.Combine(state,"history.json"),new{Selected=run.Id,Sessions=new[]{run}});
        QueueStore.Write(Path.Combine(state,"queue.json"),files);var original=File.ReadAllBytes(Path.Combine(state,"queue.json"));
        var w=new MainWindow(state);var history=(ObservableCollection<RipSession>)Field(w,"ripHistory")!;var locked=history.Single();
        Check(locked.IsLocked&&!Control<StackPanel>(w,"RipSetup").IsEnabled&&!Control<Button>(w,"InspectButton").IsEnabled&&!Control<Button>(w,"RipButton").IsEnabled&&!Control<Button>(w,"SendTuckerButton").IsEnabled,"Legacy queued runs retain their lock and cannot be resent");
        Text(w,"DiscLabel").Text="Changed unexpectedly";Call(w,"SaveHistory");w.Close();
        w=new MainWindow(state);history=(ObservableCollection<RipSession>)Field(w,"ripHistory")!;locked=history.Single();
        Check(locked.DiscLabel=="LEGACY_S1_D1"&&Text(w,"DiscLabel").Text==locked.DiscLabel,"Locked run metadata cannot be overwritten by later form changes");
        await (Task)Call(w,"RipDiscCore")!;Call(w,"InspectDisc",w,new RoutedEventArgs());Call(w,"SendToTucker",w,new RoutedEventArgs());
        Check(!(bool)Field(w,"discBusy")!&&locked.HandoffId==""&&original.SequenceEqual(File.ReadAllBytes(Path.Combine(state,"queue.json"))),"Legacy queue remains byte-for-byte intact and never processes or transfers on upgrade");
        locked.CompressionOutcome="Completed";locked.ReconciledAtUtc=DateTime.UtcNow;locked.Files[0].CompletedOutput="existing-output.mkv";Call(w,"SaveHistory");w.Close();
        w=new MainWindow(state);locked=((ObservableCollection<RipSession>)Field(w,"ripHistory")!).Single();
        Check(locked.CompressionOutcome=="Completed"&&locked.Files[0].CompletedOutput=="existing-output.mkv"&&Control<Button>(w,"SendTuckerButton").Content.ToString()=="Compression completed"&&!Control<Button>(w,"SendTuckerButton").IsEnabled,"Reconciled completion evidence remains visible and cannot be requeued");
        Call(w,"NewRipley",w,new RoutedEventArgs());
        Check(((ObservableCollection<RipSession>)Field(w,"ripHistory")!).Count==2&&!((RipSession)Field(w,"currentRipSession")!).IsLocked&&Control<StackPanel>(w,"RipSetup").IsEnabled,"New Ripley creates an editable run while retaining locked history");
        w.Close();
    }
    static async Task CheckCancellation(MainWindow w,string temp)
    {
        Call(w,"NewRipley",w,new RoutedEventArgs());Text(w,"DiscLabel").Text="ICARLY_S1_V2_D2";
        Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE","wait");var ripping=(Task)Call(w,"RipDiscCore")!;
        var progress=(RipQueue)Field(w,"ripQueue")!;await Until(()=>progress.Files.Count>0&&progress.Files[0].Bytes>0);
        var history=(ObservableCollection<RipSession>)Field(w,"ripHistory")!;var current=(RipSession)Field(w,"currentRipSession")!;var count=history.Count;
        Call(w,"NewRipley",w,new RoutedEventArgs());Control<ListBox>(w,"HistoryList").SelectedItem=history.First(h=>h.Id!=current.Id);
        Check(history.Count==count&&ReferenceEquals(Control<ListBox>(w,"HistoryList").SelectedItem,current),"An active disc session cannot be replaced by new or historical settings");
        Call(w,"ShowPage","Settings");Call(w,"OpenRipHistory",new Button{DataContext=current},new RoutedEventArgs());
        Check(Control<FrameworkElement>(w,"RipPage").Visibility==Visibility.Visible&&Control<Button>(w,"CancelRipButton").IsEnabled,"The active rip and cancel control remain reachable from Settings");
        Call(w,"CancelRip",w,new RoutedEventArgs());await ripping;Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE",null);
        Check(current.Status=="Cancelled"&&progress.Files[0].Status=="Kept · unverified"&&!Control<Button>(w,"SendTuckerButton").IsEnabled,$"Cancelling retains incomplete raw files and never offers them to Tucker ({current.Status}, {progress.Files[0].Status}, send={Control<Button>(w,"SendTuckerButton").IsEnabled}, {Control<TextBlock>(w,"RipStatus").Text})");
    }
    static List<Job> MakeDisc(string root,string label,string series,string id,string season,int count)
    {
        var ctx=new FolderContext(new SeriesIdentity("tvdb",id,series,"2007",""),season,label,new DateTime(2025,1,1),"1","1");
        var rip=RawRip.Create(root,new{series_identity=ctx.Series,season,volume_number=ctx.Volume,disc_number=ctx.DiscNumber,disc_label=label,rip_date=ctx.Date});
        for(var i=0;i<count;i++)File.WriteAllText(Path.Combine(rip.Files,$"title_t{i:00}.mkv"),"raw data");
        return rip.Publish("{season}_{disc_label}_",ctx,null,CancellationToken.None);
    }
    static async Task CheckTuckerInbox(string temp,string fake,string? renders) {
        var state=Path.Combine(temp,"handoff-ui-state");var w=new MainWindow(state);Configure(w,temp,fake);
        var raw=Path.Combine(temp,"handoff-ui-raw");var files=MakeDisc(raw,"ICARLY_S4_D1","iCarly","82493","4",2);
        var run=new RipSession{Status="Completed",DiscLabel="ICARLY_S4_D1",Files=files,RawFolder=raw,Season="4"};
        ((ObservableCollection<RipSession>)Field(w,"ripHistory")!).Add(run);Control<ListBox>(w,"HistoryList").SelectedItem=run;
        Check(Control<Button>(w,"SendTuckerButton").IsEnabled,"Completed run exposes Send to Tucker as its only onward action");
        Call(w,"SendToTucker",w,new RoutedEventArgs());
        Check(run.IsLocked&&run.HandoffStatus=="Offered"&&!File.Exists(Path.Combine(state,"queue.json"))&&w.FindName("QueueRipButton")==null&&Control<Button>(w,"SendTuckerButton").Content.ToString()=="Awaiting Tucker","Send locks the run, offers inbox work, and never adds local jobs");
        Call(w,"SendToTucker",w,new RoutedEventArgs());
        var restored=new MainWindow(state);var saved=((ObservableCollection<RipSession>)Field(restored,"ripHistory")!).Single(s=>s.Id==run.Id);
        Check(saved.IsLocked&&saved.HandoffId==run.HandoffId&&saved.HandoffStatus=="Offered","Reopening keeps the immutable handoff and pending acceptance lock");
        restored.Close();
        using(var sql=new Doggo.Sqlite(Path.Combine(state,"doggo.db")))sql.Exec("UPDATE handoff_batches SET status='Accepted' WHERE id=?",run.HandoffId);
        restored=new MainWindow(state);saved=((ObservableCollection<RipSession>)Field(restored,"ripHistory")!).Single(s=>s.Id==run.Id);
        Check(saved.HandoffStatus=="Accepted"&&saved.HandoffAcceptedUtc.HasValue&&Control<Button>(restored,"SendTuckerButton").Content.ToString()=="Accepted by Tucker","Ripley reads durable acceptance on startup when acceptance happened while Ripley was closed");
        var state2=Path.Combine(temp,"outbox-crash-state");Directory.CreateDirectory(state2);
        var pending=new RipSession{Status="Completed",DiscLabel="ICARLY_S4_D1",Files=files,RawFolder=raw,Season="4"};
        QueueStore.Write(Path.Combine(state2,"history.json"),new{Selected=pending.Id,Sessions=new[]{pending}});
        TuckerHandoff.Prepare(state2,System.Text.Json.JsonSerializer.SerializeToNode(pending)!.AsObject());
        var recovered=new MainWindow(state2);var recoveredRun=((ObservableCollection<RipSession>)Field(recovered,"ripHistory")!).Single();
        Check(recoveredRun.IsLocked&&recoveredRun.HandoffStatus=="Not delivered"&&Control<Button>(recovered,"SendTuckerButton").Content.ToString()=="Retry send to Tucker","Crash after outbox write recovers frozen run without auto-delivery or local duplication");
        var offlineState=Path.Combine(temp,"outbox-offline-state");Directory.CreateDirectory(offlineState);
        var offlineRun=new RipSession{Status="Completed",DiscLabel="ICARLY_S4_D1",Files=files,RawFolder=raw,Season="4"};
        QueueStore.Write(Path.Combine(offlineState,"history.json"),new{Selected=offlineRun.Id,Sessions=new[]{offlineRun}});
        TuckerHandoff.Prepare(offlineState,System.Text.Json.JsonSerializer.SerializeToNode(offlineRun)!.AsObject());
        var offlineManifest=File.ReadAllBytes(TuckerHandoff.ManifestPath(offlineState,offlineRun.Id));
        // A directory at the database path simulates unavailable shared storage without touching live data.
        var unavailableDatabase=Path.Combine(offlineState,"doggo.db");Directory.CreateDirectory(unavailableDatabase);
        var offline=new MainWindow(offlineState);var frozen=((ObservableCollection<RipSession>)Field(offline,"ripHistory")!).Single();
        Check(frozen.IsLocked&&frozen.HandoffStatus=="Not delivered"&&Control<Button>(offline,"SendTuckerButton").Content.ToString()=="Retry send to Tucker","Outbox recovery locks the run even when the shared database cannot open");
        var persisted=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(offlineState,"history.json")))!;
        Check(persisted["Sessions"]![0]!["HandoffId"]!.ToString()==frozen.HandoffId,"Recovered handoff lock is saved before contacting the unavailable database");
        Call(offline,"SendToTucker",offline,new RoutedEventArgs());
        Check(frozen.IsLocked&&frozen.HandoffStatus=="Not delivered"&&File.ReadAllBytes(TuckerHandoff.ManifestPath(offlineState,frozen.Id)).SequenceEqual(offlineManifest),"Failed delivery preserves the original request and leaves retry available");
        Directory.Delete(unavailableDatabase);
        Call(offline,"SendToTucker",offline,new RoutedEventArgs());Call(offline,"SendToTucker",offline,new RoutedEventArgs());
        using(var sql=new Doggo.Sqlite(unavailableDatabase))Check(frozen.HandoffStatus=="Offered"&&sql.Query("SELECT id FROM handoff_batches").Count==1&&sql.Query("SELECT id FROM handoff_events WHERE kind='Offered'").Count==1,"Retry after database recovery delivers exactly one inbox request");
        using(var sql=new Doggo.Sqlite(unavailableDatabase))sql.Exec("UPDATE handoff_batches SET status='Declined' WHERE id=?",frozen.HandoffId);
        Call(offline,"RefreshHandoffs");
        Check(frozen.IsLocked&&frozen.HandoffStatus=="Declined"&&!Control<Button>(offline,"SendTuckerButton").IsEnabled&&files.All(f=>File.Exists(f.Source)),"Declined inbox request stays locked and retains every raw file");
        offline.Close();
        if(renders!=null){Directory.CreateDirectory(renders);await Render(restored,1440,920,Path.Combine(renders,"tucker-accepted.png"));await Render(recovered,1080,740,Path.Combine(renders,"tucker-retry-minimum.png"));}
        w.Close();restored.Close();recovered.Close();
    }
    static async Task Render(MainWindow window,int width,int height,string path) {
        var root=(FrameworkElement)window.Content;root.Measure(new Size(width,height));root.Arrange(new Rect(0,0,width,height));root.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);root.UpdateLayout();
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(root);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);encoder.Save(stream);
    }
}
