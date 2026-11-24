using System.IO;
using System.Windows;
using System.Windows.Controls;
using Scout.Core;
namespace Scout.Desktop;
public partial class MainWindow {
 private readonly WorkspaceStore workspaces=new(Environment.GetEnvironmentVariable("SCOUT_WORKSPACES")??Environment.GetEnvironmentVariable("EPISCOUT_WORKSPACES")??Path.Combine(SettingsStore.DefaultDirectory,"UI-Trial","Workspaces"));
 private RunWorkspace? workspace;
 private bool openingWorkspace;
 private void RefreshHistory(){
  var history=workspaces.ReadAll(includeArchived:ShowArchivedScouts.IsChecked==true).Where(x=>!string.IsNullOrWhiteSpace(x.Library)).ToList();
  openingWorkspace=true;
  try {
   HistoryList.ItemsSource=history;
   HistoryList.SelectedItem=history.FirstOrDefault(x=>x.Id==workspace?.Id);
  } finally {openingWorkspace=false;}
  RefreshTaskbarScouts(history.Where(x=>!x.Archived).ToList());
  WorkspaceHeading.Text=workspace?.Name??"Your next Scout";
  WorkspaceSource.Text=workspace?.Library??"One Scout. One source folder. One process.";
  RefreshScoutActions();
  RefreshDiscovery();RefreshRunOptions();
  NamingChanged(this,new RoutedEventArgs());
 }
 private void RecordEvent(string text) {
  if(workspace==null)return;
  workspace.Events.Add(DateTime.Now.ToString("HH:mm")+"  "+text);workspaces.Save(workspace);
  SessionLog.Text=string.Join("\n\n",workspace.Events);WorkspaceHeading.Text=workspace.Name;WorkspaceSource.Text=workspace.Library;
  var state=workspace.Id+":"+workspace.Status;
  if(taskbarStatus!=state){taskbarStatus=state;RefreshTaskbarScouts(workspaces.ReadAll());}
 }
 private void SaveDraft() {
  if(workspace==null||workspace.Status!="Draft")return;
  workspace.Name=string.IsNullOrWhiteSpace(SeriesName.Text)?"New Scout":SeriesName.Text.Trim();
  workspace.Catalog=catalogPath;workspace.Dialogue=dialoguePath;
  workspaces.Save(workspace);
 }
 private void NewWorkspace(object? sender,RoutedEventArgs? e) {
  if(!CanSwitchScout)return;
  try {
   if(PickFolder("Choose the source video folder for this Scout") is not string folder)return;
   CreateRun(folder);
  }catch(Exception ex){Error(ex);}
 }
 private void CreateRun(string folder) {
  if(!CanSwitchScout)return;
  PersistSetup();SaveDraft();
  var inherited=workspace!=null&&string.Equals(Path.GetFullPath(workspace.Library),Path.GetFullPath(folder),StringComparison.OrdinalIgnoreCase)?workspace.RestoreSetup() with {}:null;
  var sample=inherited!=null?workspace!.DiscoveryFiles.ToList():new List<string>();
  var created=workspaces.CreateRun(folder);created.Setup=inherited??new ScoutSetup{MetadataProvider=appSettings.MetadataProvider};created.SeriesIdentity=null;created.DiscoveryFiles=[];workspaces.Save(created);operation=null;pauseRequest=null;forceStopRequested=false;ClearModelActivity();workspace=created;
  report=null;reportPath=null;previewHtml=null;groups=[];
  SeriesName.Text="";LibraryPath.Text=workspace.Library;LibraryPath.IsReadOnly=true;catalogPath="";dialoguePath="";
  outputPath=workspaces.DirectoryFor(workspace);
  ResultsGrid.ItemsSource=null;PreviewGrid.ItemsSource=null;ClearEvidence();ActivityLog.Clear();
  FileCount.Text=EpisodeCount.Text=DuplicateCount.Text=ReviewCount.Text="—";
  PreviewSummary.Text="Generate a preview from this Scout's results.";PreviewDetails.Clear();
  SessionLog.Text="";RecordEvent("New Scout created for "+workspace.Library);
  AttachSelectedWorker();RefreshHistory();ShowPage("Library");
 }
 private bool EnsureDraftScout() {
  if(workspace!=null&&workspace.Status=="Draft"&&report==null)return true;
  Status.Text="This Scout already has a saved process. Use Resume Scout to continue it, or New Scout to start another.";
  return false;
 }
 private void OpenWorkspace(object sender,SelectionChangedEventArgs e) {
  if(openingWorkspace||HistoryList.SelectedItem is not RunWorkspace chosen)return;
  if(!CanSwitchScout){Status.Text="Wait for the current operation to finish before switching.";return;}
  OpenSavedRun(chosen);
 }
 private void OpenSavedRun(RunWorkspace chosen) {
  if(!CanSwitchScout&&workspace?.Id!=chosen.Id)return;
  var foreground=CanSwitchScout?null:operation;
  try {
   SaveDraft();chosen=activeScouts.GetValueOrDefault(chosen.Id)?.Run??chosen;workspace=chosen;openingWorkspace=true;
   operation=null;pauseRequest=null;forceStopRequested=false;
   report=null;reportPath=null;previewHtml=null;groups=[];ResultsGrid.ItemsSource=null;PreviewGrid.ItemsSource=null;ClearEvidence();
   PreviewSummary.Text="Generate a preview from these results.";PreviewDetails.Clear();
   FileCount.Text=EpisodeCount.Text=DuplicateCount.Text=ReviewCount.Text="—";
   if(!activeScouts.ContainsKey(chosen.Id)&&ScoutStorage.Exists(chosen.Report))LoadReport(chosen.Report);
   SeriesName.Text=chosen.Name;LibraryPath.Text=chosen.Library;catalogPath=chosen.Catalog;dialoguePath=chosen.Dialogue;
   outputPath=workspaces.DirectoryFor(chosen);
   if(!activeScouts.ContainsKey(chosen.Id))RestorePreview();
   if(foreground==null)AttachSelectedWorker();else{operation=foreground;SetBusy(true);}
   SessionLog.Text=string.Join("\n\n",chosen.Events);ActivityLog.Text=chosen.Events.Count==0?"":string.Join("\n",chosen.Events)+"\n";
   WorkspaceHeading.Text=chosen.Name;WorkspaceSource.Text=chosen.Library;ShowPage("Session");
  }catch(Exception ex){Error(ex);}finally{openingWorkspace=false;if(foreground!=null){operation=foreground;SetBusy(true);}else AttachSelectedWorker();RefreshHistory();}
 }
 private RunWorkspace? deletedRun;
 private void HistoryFilterChanged(object sender,RoutedEventArgs e)=>RefreshHistory();
 private void HistoryItemLoaded(object sender,RoutedEventArgs e) {
  if(sender is not ListBoxItem row||row.ContextMenu!=null)return;
  // Each recycled history container owns its menu. Inline event handlers inside
  // a Style setter are wired to the wrong control type by the WPF XAML compiler.
  var menu=new ContextMenu();
  menu.SetBinding(DataContextProperty,new System.Windows.Data.Binding("PlacementTarget.DataContext"){RelativeSource=System.Windows.Data.RelativeSource.Self});
  var archive=new MenuItem{Header="Archive"};archive.Click+=ArchiveHistoryScout;
  var delete=new MenuItem{Header="Delete…"};delete.Click+=DeleteHistoryScout;
  menu.Items.Add(archive);menu.Items.Add(delete);menu.Opened+=HistoryMenuOpened;
  row.ContextMenu=menu;
 }
 private void HistoryMenuOpened(object sender,RoutedEventArgs e) {
  if(sender is not ContextMenu menu||menu.DataContext is not RunWorkspace run)return;
  var archive=(MenuItem)menu.Items[0];archive.Header=run.Archived?"Restore from archive":"Archive";
  foreach(MenuItem item in menu.Items)item.IsEnabled=operation==null&&run.Status is not ("Running" or "Queued");
 }
 private void ArchiveHistoryScout(object sender,RoutedEventArgs e) {
  if(operation!=null||(sender as MenuItem)?.DataContext is not RunWorkspace chosen)return;
  try {
   var run=workspace?.Id==chosen.Id?workspace:chosen;SaveDraft();workspaces.ArchiveRun(run,!run.Archived);
   if(run.Archived&&ShowArchivedScouts.IsChecked!=true)RemoveFromView(run);else RefreshHistory();
   Status.Text=run.Archived?"Scout archived. Use Show archived to restore it.":"Scout restored from archive.";
  }catch(Exception ex){Error(ex);}
 }
 private void DeleteHistoryScout(object sender,RoutedEventArgs e) {if((sender as MenuItem)?.DataContext is RunWorkspace chosen)DeleteHistory(workspace?.Id==chosen.Id?workspace:chosen);}
 private void DeleteRun(object sender,RoutedEventArgs e) {if(workspace!=null)DeleteHistory(workspace);}
 private void DeleteHistory(RunWorkspace run) {
  if(operation!=null||run.Status is "Running" or "Queued")return;
  if(MessageBox.Show(this,$"Delete {run.Name} from history?\n\nYou can undo this deletion. Saved data is retained for recovery; source videos are never changed.","Delete Scout",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
  try {
   workspaces.DeleteRun(run);deletedRun=run;RemoveFromView(run);
   UndoDeleteButton.Visibility=Visibility.Visible;Status.Text="Scout deleted from history. Source videos unchanged.";
  }catch(Exception ex){Error(ex);}
 }
 private void RemoveFromView(RunWorkspace removed) {
  if(workspace?.Id!=removed.Id){RefreshHistory();return;}
  workspace=null;report=null;reportPath=null;previewHtml=null;groups=[];catalogPath="";dialoguePath="";outputPath="";
  ResultsGrid.ItemsSource=null;PreviewGrid.ItemsSource=null;ClearEvidence();ActivityLog.Clear();SessionLog.Text="";
  var remaining=workspaces.ReadAll(includeArchived:ShowArchivedScouts.IsChecked==true);var next=remaining.FirstOrDefault(x=>string.Equals(x.Library,removed.Library,StringComparison.OrdinalIgnoreCase))??remaining.FirstOrDefault();
  if(next!=null)OpenSavedRun(next);else {RefreshHistory();ShowPage("Session");}
 }
 private void UndoDelete(object sender,RoutedEventArgs e) {
  if(operation!=null||deletedRun==null)return;
  try {workspaces.RestoreRun(deletedRun);if(deletedRun.Archived)ShowArchivedScouts.IsChecked=true;OpenSavedRun(deletedRun);deletedRun=null;UndoDeleteButton.Visibility=Visibility.Collapsed;Status.Text="Scout restored.";}catch(Exception ex){Error(ex);}
 }
 private void FinishWorkspace(object sender,RoutedEventArgs e) {
  if(operation!=null||workspace==null)return;
  SaveDraft();workspace.Status="Finished";RecordEvent("Scout finished. Results and previews remain available in history.");
  RefreshHistory();ShowPage("Session");
 }
 private void ImportWorkspace(string path) {
  var imported=Data.Read(path);
  var rows=imported.Array("files").Select(x=>x?.AsObject().Text("path")).Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>Path.GetFullPath(x!)).ToList();
  if(rows.Count==0)throw new InvalidDataException("A report needs source files to create a Scout.");
  var root=imported.Text("source_root");
  if(string.IsNullOrWhiteSpace(root)) {
   root=Path.GetDirectoryName(rows[0])!;
   while(rows.Any(x=>!PreviewPlanner.Within(x,root)))root=Path.GetDirectoryName(root)??throw new InvalidDataException("No shared source folder.");
  }
  if(Path.GetFileName(root).StartsWith("Season ",StringComparison.OrdinalIgnoreCase)||Path.GetFileName(root).Equals("Specials",StringComparison.OrdinalIgnoreCase))root=Path.GetDirectoryName(root)!;
  CreateRun(root);
  var directory=workspaces.DirectoryFor(workspace!);
  ScoutStorage.Copy(path,Path.Combine(directory,"report.json"));
  var catalog=Path.Combine(Path.GetDirectoryName(path)!,"catalog.json");
  if(ScoutStorage.Exists(catalog))ScoutStorage.Copy(catalog,Path.Combine(directory,"catalog.json"));
  workspace!.Report=Path.Combine(directory,"report.json");workspace.Status="Ready for review";
  // Import creates a new Scout. Report destinations describe past moves, not
  // a destination override for this new run.
  workspace.ContentOptions=ScoutContentOptions.Read(imported)??new();
 }
 private void MinimizeWindow(object sender,RoutedEventArgs e)=>SystemCommands.MinimizeWindow(this);
 private void ToggleMaximize(object sender,RoutedEventArgs e) {
  if(WindowState==WindowState.Maximized)SystemCommands.RestoreWindow(this);else SystemCommands.MaximizeWindow(this);
 }
 private void CloseWindow(object sender,RoutedEventArgs e)=>Close();
}
