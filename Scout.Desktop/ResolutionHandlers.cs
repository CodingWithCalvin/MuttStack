using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using Scout.Core;
namespace Scout.Desktop;
public partial class MainWindow
{
    private void RefreshResolutionActions() {
        if(ForceAssignmentButton==null||UndoAssignmentButton==null)return;
        var group=ResultsGrid.SelectedItem as RecordingGroup;
        bool ready=operation==null&&report!=null&&reportPath!=null&&group!=null&&!MoveExecutor.HasPending(report);
        ForceAssignmentButton.IsEnabled=ready&&!string.IsNullOrEmpty(catalogPath)&&ScoutStorage.Exists(catalogPath);
        UndoAssignmentButton.Visibility=report!=null&&group?.Rows.Any(r=>HumanResolution.For(report,r)!=null)==true?Visibility.Visible:Visibility.Collapsed;
        UndoAssignmentButton.IsEnabled=ready;
    }
    private async void ForceAssignment(object sender,RoutedEventArgs e) {
        if(operation!=null||report==null||reportPath==null||ResultsGrid.SelectedItem is not RecordingGroup group)return;
        try {
            var catalog=Data.Catalog(catalogPath);var dialog=new ForceAssignmentWindow(report,group,catalog){Owner=this};
            if(dialog.ShowDialog()!=true||dialog.SelectedRow==null||dialog.SelectedEpisode==null)return;
            var path=dialog.SelectedRow.Text("path");var fingerprint=CollectionResolver.FingerprintKey(dialog.SelectedRow["primary_fingerprint"]);
            await SaveResolution(fresh=>{
                var row=fresh.Array("files").OfType<JsonObject>().Single(r=>r.Text("path")==path&&CollectionResolver.FingerprintKey(r["primary_fingerprint"])==fingerprint);
                HumanResolution.Apply(fresh,row,dialog.SelectedEpisode,catalog,dialog.Note);
            },path,$"Human override: {Path.GetFileName(path)} assigned to {dialog.SelectedEpisode.Code} · {dialog.SelectedEpisode.Title}.");
        }catch(Exception ex){Error(ex);}
    }
    private async void UndoAssignment(object sender,RoutedEventArgs e) {
        if(operation!=null||report==null||reportPath==null||ResultsGrid.SelectedItem is not RecordingGroup group)return;
        var row=group.Rows.FirstOrDefault(r=>HumanResolution.For(report,r)!=null);if(row==null)return;
        var id=HumanResolution.For(report,row)!.Text("id");
        try{await SaveResolution(fresh=>HumanResolution.Undo(fresh,id),row.Text("path"),"Human override undone. Automatic conflict handling restored; completed file moves are unchanged.");}
        catch(Exception ex){Error(ex);}
    }
    private async Task SaveResolution(Action<JsonObject> change,string selectedPath,string message) {
        var savedPath=reportPath!;
        operation=new();SetBusy(true);CancelButton.IsEnabled=false;
        try {
            await Task.Run(()=>{var fresh=Data.Read(savedPath);change(fresh);Data.Save(savedPath,fresh);});
            openingWorkspace=true;try{LoadReport(savedPath);}finally{openingWorkspace=false;}
            currentMovePlan=null;previewHtml=null;PreviewGrid.ItemsSource=null;
            if(workspace!=null){workspace.Preview="";workspace.Status="Ready for review";workspaces.Save(workspace);}
            Filter.SelectedIndex=0;Search.Clear();FilterRows();
            ResultsGrid.SelectedItem=groups.FirstOrDefault(g=>g.Rows.Any(r=>r.Text("path")==selectedPath));
            RecordEvent(message);ShowPage("Results");Status.Text=message+" Generate a new move preview to review the effect.";
        }finally{operation?.Dispose();operation=null;SetBusy(false);RefreshHistory();RefreshResolutionActions();}
    }
}
