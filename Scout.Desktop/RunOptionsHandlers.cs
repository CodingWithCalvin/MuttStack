using System.IO;
using System.Windows;
using Scout.Core;
namespace Scout.Desktop;

public partial class MainWindow {
    private bool restoringRunOptions;
    private void RefreshRunOptions() {
        if(RunOptionsPanel==null)return;
        restoringRunOptions=true;
        try {
            RunOptionsPanel.IsEnabled=workspace!=null&&operation==null;
            IncludeBonusContent.IsChecked=workspace?.ContentOptions.IncludeBonusContent==true;
            IncludeCompilations.IsChecked=workspace?.ContentOptions.IncludeCompilations==true;
            UseAiFallback.IsChecked=workspace?.UseAiFallback==true;
            var mode=workspace==null?"default":DestinationTemplate.Mode(workspace);
            DestinationChoice.SelectedIndex=mode switch{"custom"=>1,"source"=>2,_=>0};
            DestinationRootEntry.Text=mode=="source"?"":mode=="default"?appSettings.DestinationTemplate:
                workspace!.DestinationMode==""?DestinationTemplate.Literal(workspace.DestinationRoot):workspace.DestinationTemplateOverride??"";
            DestinationRootPanel.IsEnabled=mode=="custom";
        } finally {restoringRunOptions=false;}
        RefreshRunDestinationExample();
    }
    private void DestinationOverrideEdited(object? sender,EventArgs e)=>RunOptionsChanged(sender??this,new RoutedEventArgs());
    private void RunOptionsChanged(object sender,RoutedEventArgs e) {
        if(restoringRunOptions||workspace==null||operation!=null||DestinationRootPanel==null)return;
        var mode=DestinationChoice.SelectedIndex switch{1=>"custom",2=>"source",_=>"default"};
        restoringRunOptions=true;
        try {
            if(mode=="default")DestinationRootEntry.Text=appSettings.DestinationTemplate;
            if(mode=="source")DestinationRootEntry.Text="";
            if(mode=="custom"&&workspace.DestinationMode=="source"&&string.IsNullOrWhiteSpace(DestinationRootEntry.Text))DestinationRootEntry.Text=appSettings.DestinationTemplate;
            DestinationRootPanel.IsEnabled=mode=="custom";
            workspace.ContentOptions=new(){IncludeBonusContent=IncludeBonusContent.IsChecked==true,IncludeCompilations=IncludeCompilations.IsChecked==true};
            workspace.UseAiFallback=UseAiFallback.IsChecked==true;
            workspace.DestinationMode=mode;workspace.DestinationRoot="";
            workspace.DestinationTemplateOverride=mode=="custom"?DestinationRootEntry.Text.Trim():null;
            workspace.Preview="";workspaces.Save(workspace);
        } finally {restoringRunOptions=false;}
        currentMovePlan=null;previewHtml=null;PreviewGrid.ItemsSource=null;PreviewDetails.Clear();RefreshMoveSelection();RefreshRunDestinationExample();
        PreviewSummary.Text="Run options changed. Generate a new preview before moving files.";
    }
    private void BrowseDestinationRoot(object sender,RoutedEventArgs e) {
        if(operation!=null)return;
        if(PickFolder("Choose the destination folder for this show") is string folder)DestinationRootEntry.Text=DestinationTemplate.Literal(folder);
    }
    private string? SelectedDestinationRoot() {
        if(workspace==null)return null;
        var destination=DestinationTemplate.Resolve(DestinationTemplate.ForRun(appSettings,workspace),SeriesName.Text,workspace.SeriesIdentity);
        if(destination==null)return null;
        if(PreviewPlanner.Within(outputPath,destination))throw new ArgumentException("The destination must be separate from internal Scout storage.");
        if(PreviewPlanner.Same(destination,LibraryPath.Text)||PreviewPlanner.Within(destination,LibraryPath.Text)||PreviewPlanner.Within(LibraryPath.Text,destination))
            throw new ArgumentException("Choose a destination outside the source folder, with neither folder inside the other, so later source scans contain only remaining files.");
        return destination;
    }
    private void RefreshRunDestinationExample() {
        if(RunDestinationExample==null)return;
        if(workspace==null){RunDestinationExample.Text="Select a Scout to see its destination.";return;}
        try {
            var destination=SelectedDestinationRoot();var identity=workspace.SeriesIdentity;
            var series=identity?.Name??(string.IsNullOrWhiteSpace(SeriesName.Text)?workspace.Name:SeriesName.Text);
            var root=destination??workspace.Library;
            RunDestinationExample.Text=(DestinationTemplate.Mode(workspace)=="default"?"Using global default · ":"")+(destination==null?"Organize within the source folder":"Move to "+destination)+
                "\nExample episode: "+DestinationEpisodeExample(root,appSettings.Naming,series,identity);
            RunDestinationExample.Foreground=(System.Windows.Media.Brush)FindResource("AccentBrush");
        }catch(ArgumentException ex){RunDestinationExample.Text=ex.Message;RunDestinationExample.Foreground=System.Windows.Media.Brushes.Salmon;}
    }
}
