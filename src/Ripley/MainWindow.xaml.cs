using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
namespace Ripley;

public partial class MainWindow : Window
{
    readonly string stateRoot;
    string SettingsPath => Path.Combine(stateRoot,"settings.json");
    string QueuePath => Path.Combine(stateRoot,"queue.json");
    readonly HashSet<string> legacyQueuedSources=new(StringComparer.OrdinalIgnoreCase);
    CancellationTokenSource? discCancel;
    bool discBusy, adding, namingReady, updatingSeason;
    string lastSeasonLabel="";
    DiscInfo? inspected;
    string inspectedSource="";
    SeriesIdentity? series;
    List<Job> lastRipFiles=new();
    public MainWindow() : this(AppStorage.Prepare(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))) { }
    public MainWindow(string stateDirectory,Func<List<OpticalDrive>>? driveReader=null,Func<string,bool>? confirmHistoryDeletion=null)
    {
        this.confirmHistoryDeletion=confirmHistoryDeletion;
        readOpticalDrives=driveReader??ReadOpticalDrives;stateRoot=Path.GetFullPath(stateDirectory);
        InitializeComponent();RipGrid.ItemsSource=ripQueue.Files;RawTemplate.Text=FolderNames.DefaultRaw;
        MakeMkv.Text=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"MakeMKV","makemkvcon64.exe");
        var local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        ScoutFolder.Text=Path.Combine(local,Directory.Exists(Path.Combine(local,"Episcout"))?"Episcout":"Scout");
        try {
            if(File.Exists(SettingsPath)) {
                var s=JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
                string Value(string key,string fallback="")=>s[key]?.ToString()??fallback;
                MakeMkv.Text=Value("MakeMkv",MakeMkv.Text);ScoutFolder.Text=Value("ScoutFolder",ScoutFolder.Text);
                RawFolder.Text=Value("Raw");Minimum.Text=Value("Minimum","120");Season.Text=Value("Season");
                series=s["Series"]?.Deserialize<SeriesIdentity>();AutoSeason.IsChecked=s["AutoDetectSeason"]?.GetValue<bool>()??true;
                Volume.Text=Value("VolumeNumber");DiscNumber.Text=Value("DiscNumber");FilenamePrefix.Text=Value("FilenamePrefix");RawTemplate.Text=Value("RawFormat",FolderNames.DefaultRaw);
            }
            // Legacy queue data is read solely to protect previously queued runs. It is never run or rewritten.
            foreach(var j in QueueStore.Read(QueuePath))legacyQueuedSources.Add(j.Source);
        }catch(Exception ex){Error(ex);}
        DisplaySeries();namingReady=true;UpdateNamingPreview();InitializeHistory();InitializeShell();InitializeHandoffs();
        if(legacyQueuedSources.Count>0)Status.Text="Legacy compression records are retained. Manage future compression in Tucker.";
        Closing+=(_,e)=>{if(discBusy||adding){e.Cancel=true;MessageBox.Show(this,"Finish or cancel the disc operation before closing. Raw files are kept.","Ripley is working");}else Save();};
    }
    bool Save() {
        try {
            SaveHistory();
            var s=File.Exists(SettingsPath)?JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject():new JsonObject();
            s["MakeMkv"]=MakeMkv.Text;s["Raw"]=RawFolder.Text;s["Minimum"]=int.TryParse(Minimum.Text,out var m)?m:120;s["ScoutFolder"]=ScoutFolder.Text;
            s["Series"]=JsonSerializer.SerializeToNode(series);s["Season"]=Season.Text;s["RawFormat"]=RawTemplate.Text;s["AutoDetectSeason"]=AutoSeason.IsChecked;
            s["VolumeNumber"]=Volume.Text;s["DiscNumber"]=DiscNumber.Text;s["FilenamePrefix"]=FilenamePrefix.Text;QueueStore.Write(SettingsPath,s);return true;
        }catch(Exception ex){Error(new IOException("Could not save settings: "+ex.Message,ex));return false;}
    }
    void Append(string text){Log.AppendText(text+Environment.NewLine);if(Log.Text.Length>45000)Log.Text=Log.Text[^30000..];Log.ScrollToEnd();}
    void Error(Exception ex){Status.Text=ex.Message;Append(ex.Message);}
    Action<string> DiscLog=>s=>Dispatcher.Invoke(()=>{Append(s);if(Disc.Progress(s) is double p)RipProgress.Value=p;});
    void SetDiscBusy(bool value){discBusy=value;RefreshShell();if(!value)ScheduleDriveRefresh();}
    void SetAdding(bool value){adding=value;RefreshShell();if(!value)ScheduleDriveRefresh();}
    int MinSeconds()=>int.TryParse(Minimum.Text,out var value)&&value>=0&&value<=86400?value:throw new IOException("Minimum title length must be 0–86400 seconds.");
    string NumberValue(TextBox box,string label){var s=box.Text.Trim();if(s.Length>0&&(!int.TryParse(s,out var n)||n<0))throw new IOException(label+" must be a non-negative number or blank.");return s;}
    string SeasonValue()=>NumberValue(Season,"Season");
    FolderContext NamingContext()=>new(series,SeasonValue(),DiscLabel.Text.Trim(),DateTime.Now,NumberValue(Volume,"Volume number"),NumberValue(DiscNumber,"Disc number"));
    void FolderBrowse(TextBox box,string title){var d=new OpenFolderDialog{Title=title};if(d.ShowDialog(this)==true)box.Text=d.FolderName;}
    void FileBrowse(TextBox box,string filter){var d=new OpenFileDialog{Filter=filter};if(d.ShowDialog(this)==true)box.Text=d.FileName;}
    void BrowseRaw(object s,RoutedEventArgs e)=>FolderBrowse(RawFolder,"Raw-rip destination · originals are kept");
    void BrowseScout(object s,RoutedEventArgs e)=>FolderBrowse(ScoutFolder,"Scout data folder containing scout.db");
    void BrowseMakeMkv(object s,RoutedEventArgs e)=>FileBrowse(MakeMkv,"MakeMKV console|*.exe");
    async void SearchSeries(object s,RoutedEventArgs e)
    {
        SearchButton.IsEnabled=false; SearchStatus.Text="Searching TVDB…";
        try { var result=await Tvdb.Search(SeriesQuery.Text.Trim(),ScoutFolder.Text.Trim(),CancellationToken.None); SeriesResults.ItemsSource=result; SearchStatus.Text=$"{result.Count} matches · select the correct series/year. Powered by TheTVDB."; }
        catch(Exception ex) { SearchStatus.Text=ex.Message; } finally { SearchButton.IsEnabled=true; }
    }
    void SeriesChanged(object s,SelectionChangedEventArgs e) { if(SeriesResults.SelectedItem is SeriesIdentity identity) { series=identity; DisplaySeries(); Save(); } }
    void ClearSeries(object s,RoutedEventArgs e) { series=null; SeriesResults.SelectedItem=null; DisplaySeries(); Save(); }
    void DisplaySeries() {
        SelectedSeriesText.Text=series?.Display??"No series selected · optional"; SeriesOverview.Text=series?.Overview??"";
        UpdateNamingPreview();CaptureHistory();
    }
    void NamingChanged(object sender,TextChangedEventArgs e)
    {
        if(namingReady && ReferenceEquals(sender,DiscLabel)) DetectSeason();
        UpdateNamingPreview();CaptureHistory();
    }
    void SeasonEdited(object sender,TextChangedEventArgs e)
    {
        if(namingReady && !updatingSeason) SeasonHint.Text="Manual values for this disc. Auto detection resumes when the disc label changes.";
        UpdateNamingPreview();CaptureHistory();
    }
    void AutoSeasonChanged(object sender,RoutedEventArgs e) { if(namingReady) { lastSeasonLabel="";DetectSeason();UpdateNamingPreview(); } }
    void DetectSeason()
    {
        var label=DiscLabel.Text.Trim();
        if(AutoSeason.IsChecked!=true) { SeasonHint.Text="Manual values · update them when changing discs.";return; }
        if(label==lastSeasonLabel) return;
        lastSeasonLabel=label;var hints=DiscLabelHints.Parse(label);
        updatingSeason=true;
        try {
            Season.Text=hints.Season?.ToString(System.Globalization.CultureInfo.InvariantCulture)??"";
            Volume.Text=hints.Volume?.ToString(System.Globalization.CultureInfo.InvariantCulture)??"";
            DiscNumber.Text=hints.DiscNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture)??"";
        }
        finally { updatingSeason=false; }
        SeasonHint.Text="Values found in the disc label are filled automatically. Fill in missing values or edit them for this disc.";
    }
    void SeriesQueryKeyDown(object sender,System.Windows.Input.KeyEventArgs e) { if(e.Key==System.Windows.Input.Key.Enter && SearchButton.IsEnabled) { e.Handled=true; SearchSeries(sender,new()); } }
    void UpdateNamingPreview()
    {
        if(!namingReady) return;
        UpdateOutputSummary();
        FolderContext context;
        try { context=NamingContext(); } catch(Exception ex) { RawPathPreview.Text=FilenamePreview.Text=ex.Message;return; }
        string PreviewPath(string root,string format) { try { return FolderNames.Resolve(root.Trim(),format,context,false); } catch(Exception ex) { return ex.Message; } }
        RawPathPreview.Text=PreviewPath(RawFolder.Text,RawTemplate.Text);
        UpdateOutputSummary();
        try {
            var original=inspected?.Titles.FirstOrDefault(t=>t.Qualifies(MinSeconds())&&t.Value(27).Length>0)?.Value(27);
            FilenamePreview.Text=(original==null?"Example: ":"Filename: ")+FileNames.WithPrefix(FilenamePrefix.Text,original??"title_t00.mkv",context);
        } catch(Exception ex) { FilenamePreview.Text=ex.Message; }
    }
    async void InspectDisc(object s,RoutedEventArgs e)
    {
        if(RipLocked){Status.Text=LockedRipMessage;return;}
        if(discBusy||driveScanRunning)return;
        if(DriveChoice.SelectedItem is not OpticalDrive drive) { RipStatus.Text="Refresh and select an optical drive first."; return; }
        EnsureRipSession();SetDiscBusy(true); discCancel=new(); RipProgress.Value=0;SetRipSessionStatus("Inspecting");
        try { RipStatus.Text="Inspecting title and track metadata…"; inspected=await Disc.Inspect(MakeMkv.Text.Trim(),drive.Source,DiscLog,discCancel.Token); inspectedSource=drive.Source;
            if(inspected.Label.Length>0) DiscLabel.Text=inspected.Label;
            UpdateNamingPreview();
            var min=MinSeconds();PrepareRipQueue(inspected,min);RipSetupScroll.ScrollToTop(); RipStatus.Text=$"{inspected.Label} · {inspected.Titles.Count} titles · {inspected.Titles.Count(t=>t.Qualifies(min))} meet the {min}s cutoff.";
        } catch(OperationCanceledException) { RipStatus.Text="Inspection cancelled."; } catch(Exception ex) { RipStatus.Text=ex.Message; } finally { SetRipSessionStatus("Draft");discCancel.Dispose();discCancel=null;SetDiscBusy(false); }
    }
    void ShowDiscDetails(object s,RoutedEventArgs e)
    {
        if(inspected==null) { RipStatus.Text="Inspect a disc first."; return; }
        int minimum; try { minimum=MinSeconds(); } catch(Exception ex) { Error(ex);return; }
        var text=$"Disc: {inspected.Label}\nAll qualifying titles are ripped. Episode names are identified later in Scout.\n\n"+string.Join("\n\n",inspected.Titles.Select(t=>$"Title {t.Id} · {t.Name}\n{t.Duration} · {t.Chapters} chapters · {t.Size} · {(t.Qualifies(minimum)?"RIP":"SKIP: under cutoff")}\n{t.Tracks}"));
        new Window{Owner=this,Title="Disc metadata · before ripping",Width=850,Height=600,WindowStartupLocation=WindowStartupLocation.CenterOwner,Content=new TextBox{Text=text,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new Thickness(18)}}.ShowDialog();
    }
    void CancelRip(object s,RoutedEventArgs e) { discCancel?.Cancel(); CancelRipButton.IsEnabled=false; RipStatus.Text="Stopping disc operation. Any raw files already written are kept."; }
    async void RipDisc(object s,RoutedEventArgs e) => await RipDiscCore();
    async Task RipDiscCore()
    {
        if(RipLocked){Status.Text=LockedRipMessage;return;}
        if(discBusy||driveScanRunning) return;
        if(DriveChoice.SelectedItem is not OpticalDrive drive) { RipStatus.Text="Refresh and select an optical drive first.";return; }
        EnsureRipSession();SetDiscBusy(true); discCancel=new(); var token=discCancel.Token; string? folder=null;RawRip? activeRip=null;
        RipQueuePanel.Visibility=Visibility.Collapsed;lastRipFiles.Clear();SetRipSessionStatus("Ripping");RefreshShell();
        try {
            var raw=RawFolder.Text.Trim();
            if(!Path.IsPathFullyQualified(raw)) throw new IOException("Choose an absolute raw-rip folder.");
            var min=MinSeconds(); var season=SeasonValue(); var identity=series;
            var cli=MakeMkv.Text.Trim(); var label=DiscLabel.Text.Trim();
            var rawFormat=RawTemplate.Text;var filenamePrefix=FilenamePrefix.Text;var started=DateTime.Now;
            var volume=NumberValue(Volume,"Volume number");var discNumber=NumberValue(DiscNumber,"Disc number");
            Save(); RipStatus.Text="Inspecting the inserted disc…";
            var current=await Disc.Inspect(cli,drive.Source,DiscLog,token);
            if(inspected!=null&&inspectedSource==drive.Source&&inspected.Fingerprint!=current.Fingerprint) { inspected=null; throw new IOException("The disc changed since inspection. Inspect it again before ripping."); }
            inspected=current; inspectedSource=drive.Source;
            if(!current.Titles.Any(t=>t.Qualifies(min))) throw new IOException("No titles meet the minimum length.");
            PrepareRipQueue(current,min);RipSetupScroll.ScrollToTop();
            if(label.Length==0) {
                label=current.Label;DiscLabel.Text=label;
                if(AutoSeason.IsChecked==true) { season=SeasonValue();volume=NumberValue(Volume,"Volume number");discNumber=NumberValue(DiscNumber,"Disc number"); }
            }
            var context=new FolderContext(identity,season,label,started,volume,discNumber);
            var rawPath=FolderNames.Resolve(raw,rawFormat,context);
            foreach(var title in current.Titles.Where(t=>t.Qualifies(min))) FileNames.WithPrefix(filenamePrefix,title.Value(27).Length>0?title.Value(27):"title_t00.mkv",context);
            var rip=RawRip.Create(rawPath,new { series_identity=identity,season,volume_number=volume,disc_number=discNumber,disc_label=label,rip_date=started,minimum_title_seconds=min,source=drive.Source,disc=current,raw_format=rawFormat,filename_prefix=filenamePrefix });
            folder=rip.Session;
            activeRip=rip;lastRipFilesRead=DateTime.MinValue;
            RawPathPreview.Text=rawPath;UpdateNamingPreview();
            RipStatus.Text="Ripping to "+rawPath; RipProgress.Value=0;
            using(var log=new StreamWriter(rip.Log)) {
                var gate=new object(); await Disc.Rip(cli,drive.Source,min,rip.Files,l=>{ lock(gate) log.WriteLine(l); Dispatcher.Invoke(()=>{Append(l);TrackRip(l,rip,filenamePrefix,context);}); },token);
            }
            TrackRip("",rip,filenamePrefix,context,true);ripQueue.Publishing();RefreshRipQueue();
            RipStatus.Text="Applying filenames and keeping raw files…";
            var result=await Task.Run(()=>rip.Publish(filenamePrefix,context,null,token));
            folder=rawPath;lastRipFiles=result;
            ripQueue.Complete(result);RefreshRipQueue();
            RipProgress.Value=100;
            RipStatus.Text=$"Rip complete · {result.Count} raw files saved. Send this run to Tucker’s inbox when ready.";
            SetRipSessionStatus("Completed");inspected=null;
        } catch(OperationCanceledException) { if(activeRip!=null){ripQueue.Stop(true);RefreshRipQueue();} RipStatus.Text="Rip cancelled. Raw files kept; this disc was not queued.";SetRipSessionStatus("Cancelled"); }
        catch(Exception ex) { if(activeRip!=null){ripQueue.Stop(false);RefreshRipQueue();} RipStatus.Text=ex.Message; Append(ex.Message);SetRipSessionStatus("Failed"); }
        finally { if(folder!=null) Append("Raw files retained: "+folder); discCancel.Dispose();discCancel=null;SetDiscBusy(false); }
    }
}
