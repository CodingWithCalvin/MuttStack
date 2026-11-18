using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
namespace Tucker;

public record Preferences(string Cli,string PresetFile,string GuiSettings,string Output,string Format,string Prefix,string Preset,int Decoder,string FilenameFormat="{filename}");
public record QueueTile(string Id,string Name,string Summary,bool Archived=false,bool CanManage=true) { public string ArchiveAction=>Archived?"Restore":"Archive"; }
public record DraftSource(string Id,string Display);
public partial class MainWindow : Window
{
    readonly string root;
    public QueueManager Manager {get;}
    readonly ObservableCollection<string> folders=new();
    List<Job> preview=new();
    readonly ObservableCollection<Handoff> stagedBatches=new();
    Handoff[] previewBatches=[];
    string? appendRunId;
    string appendRunName="";
    bool refreshing,busy;
    string progressJob="";
    string page="New";
    readonly System.Windows.Threading.DispatcherTimer inboxTimer=new(){Interval=TimeSpan.FromSeconds(5)};
    public MainWindow(string stateRoot,string databasePath,Func<string,bool>? confirmRunDeletion=null) {
        this.confirmRunDeletion=confirmRunDeletion;
        root=stateRoot;Directory.CreateDirectory(root);Manager=new(root,databasePath);
        InitializeComponent();
        folders.CollectionChanged+=(_,_)=>InvalidatePreview();stagedBatches.CollectionChanged+=(_,_)=>InvalidatePreview();
        Recursive.Checked+=(_,_)=>InvalidatePreview();Recursive.Unchecked+=(_,_)=>InvalidatePreview();
        var roaming=Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Cli.Text=Path.Combine(AppContext.BaseDirectory,"handbrake","HandBrakeCLI.exe");
        if(!File.Exists(Cli.Text))Cli.Text="HandBrakeCLI.exe";
        PresetFile.Text=Path.Combine(roaming,"HandBrake","presets.json");GuiSettings.Text=Path.Combine(roaming,"HandBrake","settings.json");
        OutputFormat.Text="{disc_label}";Hardware.SelectedIndex=0;
        var saved=Manager.Database.Settings<Preferences>();
        if(saved!=null){Cli.Text=saved.Cli;PresetFile.Text=saved.PresetFile;GuiSettings.Text=saved.GuiSettings;Destination.Text=saved.Output;OutputFormat.Text=saved.Format;Prefix.Text=saved.Prefix;Hardware.SelectedIndex=saved.Decoder;FilenameFormat.Text=saved.FilenameFormat;}
        ReloadPresets(saved?.Preset);
        Destination.TextChanged+=FolderFormatChanged;OutputFormat.TextChanged+=FolderFormatChanged;RefreshFolderExamples();
        Manager.Changed+=Refresh;
        Manager.Line+=line=>Dispatcher.Invoke(()=>{Append(line);var m=Regex.Match(line,@"(\d+(?:\.\d+)?)\s*%");if(m.Success&&double.TryParse(m.Groups[1].Value,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var n))Progress.Value=n;UpdateProgress();Speed.Text=line.Contains("fps")||line.Contains("ETA")?line:Speed.Text;});
        StoragePath.Text=databasePath;Refresh();OpenRun(QueueList.Items.Cast<QueueTile>().FirstOrDefault()?.Id);RefreshInboxStatus();
        Status.Text="Ready. Saved Tucker runs never start automatically.";
        Closing+=(_,e)=>{if(Manager.Running||busy){e.Cancel=true;MessageBox.Show(this,"Pause after the current file or stop the encode before closing Tucker.","Tucker is working");}else inboxTimer.Stop();};
        inboxTimer.Tick+=(_,_)=>RefreshInboxStatus();inboxTimer.Start();
    }
    void Append(string text){Log.AppendText(text+Environment.NewLine);if(Log.Text.Length>50000)Log.Text=Log.Text[^35000..];Log.ScrollToEnd();}
    void Error(Exception ex){Status.Text=ex.Message;Append(ex.Message);}
    void Refresh() {
        if(!Dispatcher.CheckAccess()){Dispatcher.Invoke(Refresh);return;}
        var selected=(QueueList.SelectedItem as QueueTile)?.Id;
        var groups=HistoryTiles();
        refreshing=true;QueueList.ItemsSource=groups;QueueList.SelectedItem=groups.FirstOrDefault(g=>g.Id==selected);refreshing=false;RefreshControls();
    }
    void RefreshControls() {
        FilterQueue();var visible=SelectedJobs();
        StartAll.IsEnabled=!Manager.Running&&!busy&&ActiveJobs().Any(CanRun);
        StartSelected.IsEnabled=!Manager.Running&&!busy&&visible.Any(CanRun);
        PauseButton.IsEnabled=StopButton.IsEnabled=Manager.Running;
        PauseButton.Visibility=StopButton.Visibility=Manager.Running?Visibility.Visible:Visibility.Collapsed;
        PauseButton.Content=Manager.PausePending?"Keep processing":"Pause after current file";
        AddMoreButton.IsEnabled=!busy&&visible.Count>0;
        AddWaiting.IsEnabled=!busy&&preview.Count>0;AddRunning.IsEnabled=AddWaiting.IsEnabled&&Manager.CanJoinRunning;
        AddWaiting.Content=appendRunId==null?"Create Tucker":"Add as waiting";
        AddRunning.Content=appendRunId==null?"Create & add to active processing":"Add to active processing";
        CancelAdditions.Visibility=appendRunId==null?Visibility.Collapsed:Visibility.Visible;CancelAdditions.IsEnabled=!busy;
        PreviewButton.Content=appendRunId==null?"Preview combined run":"Preview additions";
        UseInboxBatches.Content=appendRunId==null?"Add selected to New Tucker":"Add selected to this Tucker";
        PreviewButton.IsEnabled=!busy&&(folders.Count>0||stagedBatches.Count>0);
        ImportHandoff.IsEnabled=NewTuckerButton.IsEnabled=SourceControls.IsEnabled=UseInboxBatches.IsEnabled=!busy;
        Summary.Text=RunSummary(visible);
        QueueState.Text=visible.Count>0?"Each file keeps its saved preset and output path.\nSelect a file to review its encoding settings.":"Select a Tucker from history or create a new run.";
        if(progressJob!=(Manager.Current?.Id??"")){progressJob=Manager.Current?.Id??"";Progress.Value=0;Speed.Text="";}
        CurrentFile.Text=Manager.Current?.Source??"";ActiveEncoding.Visibility=Manager.Running?Visibility.Visible:Visibility.Collapsed;
        if(Manager.Current==null){Progress.Value=0;Speed.Text="";}
        var all=ActiveJobs();
        OverallSummary.Text=all.Count==0?"No active runs":$"{all.Count(j=>j.Status=="Completed")} / {all.Count} files complete · {all.Count(CanRun)} waiting"+(all.Any(j=>j.Status=="Needs review")?" · needs review":"");
        if(Manager.PausePending)OverallSummary.Text+=" · pausing after this file";
        UpdateProgress();RefreshNavigation();RefreshHistoryControls();if(page=="Settings")RefreshFolderExamples();
    }
    static bool CanRun(Job j)=>j.Status is "Waiting" or "Paused" or "Failed";
    static string RunSummary(List<Job> jobs){if(jobs.Count==0)return "No run selected";var status=jobs.Any(j=>j.Status is "Encoding" or "Publishing")?"Running":jobs.Any(j=>j.Status=="Needs review")?"Needs review":jobs.Any(j=>j.Status=="Failed")?"Failed":jobs.Any(j=>j.Status=="Paused")?"Paused":jobs.Any(j=>j.Status!="Completed")?"Waiting":"Completed";return $"{status} · {jobs.Count(j=>j.Status=="Completed")} / {jobs.Count} files complete";}
    List<Job> SelectedJobs(){var id=(QueueList.SelectedItem as QueueTile)?.Id;return Manager.State.Jobs.Where(j=>j.QueueId==id).ToList();}
    void UpdateProgress(){double Value(List<Job> jobs)=>jobs.Count==0?0:100*(jobs.Count(j=>j.Status=="Completed")+(Manager.Current is {} current&&current.Status!="Completed"&&jobs.Contains(current)?Progress.Value/100:0))/jobs.Count;OverallProgress.Value=Value(ActiveJobs());RunProgress.Value=Value(SelectedJobs());OverallProgress.Visibility=ActiveJobs().Count>0?Visibility.Visible:Visibility.Collapsed;}
    void FilterQueue(){var selected=(QueueGrid.SelectedItem as Job)?.Id;var jobs=SelectedJobs();QueueGrid.ItemsSource=jobs;QueueGrid.SelectedItem=jobs.FirstOrDefault(j=>j.Id==selected);if(QueueGrid.SelectedItem==null)Paths.Text="Select a file to see its input, output and saved encoding settings.";}
    void QueueSelected(object s,SelectionChangedEventArgs e){if(!refreshing&&QueueList.SelectedItem!=null){RefreshControls();ShowPage("Queue");}}
    void Details(object s,SelectionChangedEventArgs e){if(((DataGrid)s).SelectedItem is Job j)Paths.Text=$"INPUT\n{j.Source}\n\nOUTPUT\n{j.OutputPath}\n\n{j.PresetDisplay} · {j.Encoding?.Encoder} · decoding {j.Encoding?.HardwareDecode}\n{j.Error}";}
    void PreviewDetails(object s,SelectionChangedEventArgs e){if(PreviewGrid.SelectedItem is Job j)PreviewPaths.Text=$"INPUT\n{j.Source}\n\nOUTPUT\n{j.OutputPath}\n\n{j.Series?.Display}\nSeason {j.Season} · Volume {j.VolumeNumber} · Disc {j.DiscNumber}";}
    void Navigate(object s,RoutedEventArgs e)=>ShowPage((string)((Button)s).Tag);
    void OpenRun(string? id){refreshing=true;QueueList.SelectedItem=QueueList.Items.Cast<QueueTile>().FirstOrDefault(q=>q.Id==id);refreshing=false;Refresh();ShowPage(QueueList.SelectedItem==null?"New":"Queue");}
    void ResetDraft(){appendRunId=null;appendRunName="";draftRunName="";folders.Clear();stagedBatches.Clear();InvalidatePreview();}
    void NewTucker(object s,RoutedEventArgs e){if(busy)return;ResetDraft();showArchived=false;OpenRun(null);Status.Text="Combine existing rips and inbox batches, then preview the whole run.";}
    void AddMoreSources(object s,RoutedEventArgs e){if(busy||QueueList.SelectedItem is not QueueTile {Archived:false} run)return;ResetDraft();appendRunId=run.Id;appendRunName=run.Name;Refresh();ShowPage("New");Status.Text="New files use the current output and preset preferences. Existing files keep their saved settings.";}
    void CancelAppend(object s,RoutedEventArgs e){if(busy)return;var target=appendRunId;ResetDraft();OpenRun(target);Status.Text="Additions cancelled. No inbox batches were accepted.";}
    void InvalidatePreview(){preview.Clear();folderExampleJobs=[];previewBatches=[];PreviewGrid.ItemsSource=null;PreviewPaths.Clear();UpdateSuggestedRunName();SourcesList.ItemsSource=folders.Select(f=>new DraftSource("folder:"+f,"Folder · "+f)).Concat(stagedBatches.Select(h=>new DraftSource(h.Manifest.Id,$"Inbox · {h.Manifest.Files.Count} files · {string.Join(", ",h.Manifest.Files.Select(f=>f.DiscLabel).Distinct())}"))).ToList();SourceSummary.Text=$"Sources · {folders.Count} folders · {stagedBatches.Count} inbox batches";SourcesList.Visibility=folders.Count+stagedBatches.Count>0?Visibility.Visible:Visibility.Collapsed;SetSourceEditor(true);PreviewSummary.Text=$"{folders.Count} folders · {stagedBatches.Count} inbox batches. Preview combines the selected sources and skips duplicate files.";Refresh();}
    void SetSourceEditor(bool open){SourceInputs.Visibility=open?Visibility.Visible:Visibility.Collapsed;ToggleSourcesButton.Content=open?"Hide sources":"Edit sources";}
    void ToggleSources(object s,RoutedEventArgs e)=>SetSourceEditor(SourceInputs.Visibility!=Visibility.Visible);
    void ShowPage(string name){page=name;QueuePage.Visibility=name=="Queue"?Visibility.Visible:Visibility.Collapsed;NewTuckerPage.Visibility=name=="New"?Visibility.Visible:Visibility.Collapsed;SettingsPage.Visibility=name=="Settings"?Visibility.Visible:Visibility.Collapsed;InboxPage.Visibility=name=="Inbox"?Visibility.Visible:Visibility.Collapsed;ActivityPage.Visibility=name=="Activity"?Visibility.Visible:Visibility.Collapsed;RefreshNavigation();if(name=="Inbox")RefreshInbox();if(name=="Settings")RefreshFolderExamples(true);}
    void RefreshNavigation(){Heading.Text=page switch{"Queue"=>(QueueList.SelectedItem as QueueTile)?.Name??"Tucker","New"=>appendRunId==null?(draftRunName.Length==0?"New Tucker":draftRunName):"Add to "+appendRunName,"Inbox"=>"Inbox","Settings"=>"Output & settings",_=>"Activity"};foreach(var b in new[]{NavInbox,NavSettings,NavActivity})b.Style=(Style)FindResource((string)b.Tag==page?"NavActive":"NavButton");}
    void MinimizeWindow(object s,RoutedEventArgs e)=>SystemCommands.MinimizeWindow(this);
    void ToggleMaximize(object s,RoutedEventArgs e){if(WindowState==WindowState.Maximized)SystemCommands.RestoreWindow(this);else SystemCommands.MaximizeWindow(this);}
    void CloseWindow(object s,RoutedEventArgs e)=>Close();
    void AddFolders(object s,RoutedEventArgs e){var d=new OpenFolderDialog{Multiselect=true,Title="Choose completed raw-rip folders"};if(d.ShowDialog(this)==true)foreach(var f in d.FolderNames)if(!folders.Contains(f,StringComparer.OrdinalIgnoreCase))folders.Add(f);Refresh();}
    void RemoveSources(object s,RoutedEventArgs e){if(busy)return;foreach(var source in SourcesList.SelectedItems.Cast<DraftSource>().ToArray()){if(source.Id.StartsWith("folder:"))folders.Remove(source.Id[7..]);else if(stagedBatches.FirstOrDefault(b=>b.Manifest.Id==source.Id) is {} batch)stagedBatches.Remove(batch);}}
    void BrowseOutput(object s,RoutedEventArgs e){var d=new OpenFolderDialog{Title="Choose compressed output base folder"};if(d.ShowDialog(this)==true)Destination.Text=d.FolderName;}
    void BrowseFile(object s,RoutedEventArgs e){var target=(string)((Button)s).Tag;var d=new OpenFileDialog{Filter=target=="Cli"?"HandBrakeCLI|*.exe":"JSON files|*.json"};if(d.ShowDialog(this)==true){((TextBox)FindName(target)).Text=d.FileName;if(target=="PresetFile")ReloadPresets();}}
    void ReloadPresets(string? name=null){try{var list=Presets.Load(PresetFile.Text.Trim());PresetChoice.ItemsSource=list;PresetChoice.SelectedItem=list.FirstOrDefault(p=>p.Name==name)??list.FirstOrDefault(p=>p.Name=="MKV x265 High Quality - 1080p RF18")??list.FirstOrDefault();}catch(Exception ex){PresetChoice.ItemsSource=null;PresetSummary.Text=ex.Message;}}
    void LoadPresets(object s,RoutedEventArgs e)=>ReloadPresets((PresetChoice.SelectedItem as HandBrakePreset)?.Name);
    void PresetChanged(object s,SelectionChangedEventArgs e){if(PresetChoice.SelectedItem is HandBrakePreset p)PresetSummary.Text=$"{p.Group}{p.Name}\nEncoder {p.Data["VideoEncoder"]} · RF {p.Data["VideoQualitySlider"]} · speed {p.Data["VideoPreset"]} · {p.Container}\nEvery setting is frozen when you preview. No built-in preset substitution.";}
    Options Snapshot(){if(PresetChoice.SelectedItem is not HandBrakePreset p)throw new IOException("Choose an installed GUI or exported custom preset.");if(!Path.IsPathFullyQualified(Destination.Text.Trim()))throw new IOException("Choose an absolute output directory in Output & settings.");var hw=Hardware.SelectedIndex switch{1=>"software",2=>"nvdec",3=>"qsv",_=>Presets.GuiHardware(GuiSettings.Text.Trim())};var o=Presets.Snapshot(PresetFile.Text.Trim(),p,Cli.Text.Trim(),Destination.Text.Trim(),Path.Combine(root,"presets"),hw);Manager.Database.Freeze(o);return o;}
    void SaveSettings(object s,RoutedEventArgs e){try{Manager.Database.Settings(new Preferences(Cli.Text.Trim(),PresetFile.Text.Trim(),GuiSettings.Text.Trim(),Destination.Text.Trim(),OutputFormat.Text,Prefix.Text,(PresetChoice.SelectedItem as HandBrakePreset)?.Name??"",Hardware.SelectedIndex,FilenameFormat.Text));Status.Text="Preferences saved. Existing queued batches keep their frozen settings.";}catch(Exception ex){Error(ex);}}
    async void CheckRuntime(object s,RoutedEventArgs e){if(busy)return;busy=true;Refresh();try{await Presets.Validate(Snapshot(),CancellationToken.None);Status.Text="Preset import and encoder/decoder support verified. No encode was started.";}catch(Exception ex){Error(ex);}finally{busy=false;Refresh();}}
    async void Preview(object s,RoutedEventArgs e)=>await PreparePreview();
    async Task PreparePreview() {
        if(busy)return;busy=true;preview.Clear();previewBatches=[];PreviewGrid.ItemsSource=null;Refresh();
        try {
            if(appendRunId!=null&&!Manager.State.Jobs.Any(j=>j.QueueId==appendRunId))throw new IOException("The target Tucker was removed. Cancel these additions and choose another run.");
            var batches=stagedBatches.ToArray();var offered=Manager.Database.Inbox();
            if(batches.Any(h=>!offered.Any(r=>r.Id==h.Manifest.Id&&r.Status=="Offered"&&r.Digest==h.Digest)))throw new IOException("An inbox batch changed or is no longer waiting. Remove it and refresh your selection.");
            var options=Snapshot();var format=OutputFormat.Text;var prefix=Prefix.Text;var roots=folders.ToArray();var preset=((HandBrakePreset)PresetChoice.SelectedItem).Name;
            var recursive=Recursive.IsChecked==true;var filenameFormat=FilenameFormat.Text;
            var existing=Manager.State.Jobs.Select(j=>new Job{Name=j.Name,Encoding=j.Encoding}).ToArray();
            preview=await Task.Run(()=>RunPlanner.Plan(roots,batches,options,recursive,format,prefix,filenameFormat,existing));
            foreach(var j in preview){j.PresetDisplay=preset;j.Status=(Manager.State.ImportedSources.Contains(QueueManager.SourceKey(j))||Manager.Database.CompletedInRipley(QueueManager.SourceKey(j)))?"Already imported":"Ready";}
            folderExampleJobs=preview.ToArray();previewBatches=batches;UpdateSuggestedRunName();PreviewGrid.ItemsSource=preview;SetSourceEditor(preview.Count==0);PreviewSummary.Text=$"{preview.Count} unique files · {batches.Length} inbox batches · {preview.Count(j=>j.Status=="Already imported")} previously imported · {preset}\n{(appendRunId==null?"One Tucker run. Every file keeps its own metadata and output path.":"Current output and preset preferences apply only to these additions.")}";ShowPage("New");
            Status.Text=preview.Count==0?"No completed MKVs found.":appendRunId==null?"Review paths, then create the run. Inbox acceptance happens only when the run is saved.":"Choose waiting or active processing. Inbox batches are accepted when additions are saved.";
        }catch(Exception ex){preview.Clear();previewBatches=[];Error(ex);}finally{busy=false;Refresh();}
    }
    void AddPreview(object s,RoutedEventArgs e){if(busy||preview.Count==0)return;try{var running=ReferenceEquals(s,AddRunning);var target=appendRunId;var result=target==null?Manager.AddRun(preview,running,previewBatches,draftRunName):(Files:Manager.AppendRun(target,preview,running,previewBatches),Id:target);ResetDraft();showArchived=false;Refresh();OpenRun(result.Files>0||target!=null?result.Id:QueueList.Items.Cast<QueueTile>().FirstOrDefault()?.Id);RefreshInboxStatus();Status.Text=result.Files==0?"All sources were already imported; no duplicate files were added.":$"{result.Files} files {(target==null?"saved in a new Tucker":"added to this Tucker")}. "+(running?"Included in active processing.":"Saved as waiting; these additions will wait for Start.");}catch(Exception ex){Error(ex);}}
    async void StartQueues(object s,RoutedEventArgs e){var selected=(QueueList.SelectedItem as QueueTile)?.Id;if(ReferenceEquals(s,StartSelected)&&selected==null)return;try{await Manager.Start(ReferenceEquals(s,StartSelected)?selected:null);Status.Text="Processing stopped. Completed output is saved; remaining work waits for Start.";}catch(Exception ex){Error(ex);}}
    void Pause(object s,RoutedEventArgs e)=>Manager.TogglePause();
    void Stop(object s,RoutedEventArgs e){if(MessageBox.Show(this,"Stop interrupts the current encode. Its incomplete output is discarded and this file must start again later. Raw sources and completed output are kept.","Stop encoding now?",MessageBoxButton.YesNo,MessageBoxImage.Warning)==MessageBoxResult.Yes)Manager.Stop();}
    void UpdateInboxBadge(int count){InboxCount.Text=count>99?"99+":count.ToString();InboxBadge.Visibility=count>0?Visibility.Visible:Visibility.Collapsed;var label=count==0?"Inbox":$"Inbox · {count} {(count==1?"batch":"batches")} awaiting review";NavInbox.ToolTip=label;System.Windows.Automation.AutomationProperties.SetName(NavInbox,label);}
    void RefreshInboxStatus(){if(page=="Inbox"){RefreshInbox();return;}try{UpdateInboxBadge(Manager.Database.PendingInboxCount());}catch(Exception ex){Error(ex);}}
    void RefreshInbox(){try{var selected=InboxGrid.SelectedItems.Cast<InboxItem>().Select(i=>i.Id).ToHashSet();var rows=Manager.Database.Inbox();InboxGrid.ItemsSource=rows;foreach(var row in rows.Where(r=>selected.Contains(r.Id)))InboxGrid.SelectedItems.Add(row);var pending=rows.Count(r=>r.Status=="Offered");UpdateInboxBadge(pending);InboxSummary.Text=$"{pending} awaiting review · {rows.Count(r=>r.Status=="Accepted")} accepted · {stagedBatches.Count} staged for {(appendRunId==null?"New Tucker":appendRunName)}";}catch(Exception ex){Error(ex);}}
    void ImportManifest(object s,RoutedEventArgs e){var d=new OpenFileDialog{Filter="Doggo handoff JSON|*.json"};if(d.ShowDialog(this)!=true)return;try{Manager.Database.Offer(Handoff.Read(d.FileName));RefreshInbox();Status.Text="Handoff saved to the inbox. Review it before accepting.";}catch(Exception ex){Error(ex);}}
    void ReviewHandoff(object s,RoutedEventArgs e){if(busy)return;var selected=InboxGrid.SelectedItems.Cast<InboxItem>().Where(i=>i.Status=="Offered").ToArray();if(selected.Length==0){Status.Text="Select one or more waiting inbox batches.";return;}foreach(var item in selected)if(stagedBatches.All(h=>h.Manifest.Id!=item.Id))stagedBatches.Add(new(JsonSerializer.Deserialize<HandoffManifest>(item.Payload)!,item.Digest));ShowPage("New");Status.Text=$"{stagedBatches.Count} inbox batches in this Tucker. Add more folders or batches, then preview together.";}
    void DeclineHandoff(object s,RoutedEventArgs e){if(InboxGrid.SelectedItem is not InboxItem {Status:"Offered"} item)return;try{Manager.Database.Decline(item.Id);RefreshInbox();Status.Text="Handoff declined. Source files were kept.";}catch(Exception ex){Error(ex);}}
    void ExportReceipt(object s,RoutedEventArgs e){if(InboxGrid.SelectedItem is not InboxItem {Status:"Accepted"} item)return;var receipt=Manager.State.Receipts.FirstOrDefault(r=>r.Id==item.Id);if(receipt==null)return;var d=new SaveFileDialog{Filter="JSON receipt|*.json",FileName=item.Id+".accepted.json"};if(d.ShowDialog(this)==true)try{QueueStore.Write(d.FileName,receipt);}catch(Exception ex){Error(ex);}}
}
