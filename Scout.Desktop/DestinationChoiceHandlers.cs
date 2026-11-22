using System.IO;
using System.Windows;
using Scout.Core;
namespace Scout.Desktop;

public partial class MainWindow {
 private void RefreshDestinationChoices() {
  if(KeepBothDestinationButton==null)return;
  var item=(PreviewGrid.SelectedItem as PreviewRow)?.Item;
  bool enabled=operation==null&&report!=null&&!MoveExecutor.HasPending(report)&&item!=null&&item.Action!="completed";
  KeepBothDestinationButton.IsEnabled=enabled&&item!.DestinationConflict;
  SkipDestinationButton.IsEnabled=enabled&&(item!.DestinationConflict||item.DestinationChoice=="both");
  ResetDestinationButton.IsEnabled=enabled&&item!.DestinationChoice.Length>0;
 }
 private void KeepBothDestination(object sender,RoutedEventArgs e)=>SaveDestinationChoice("both");
 private void SkipDestination(object sender,RoutedEventArgs e)=>SaveDestinationChoice("skip");
 private void ResetDestination(object sender,RoutedEventArgs e)=>SaveDestinationChoice("reset");
 private async void SaveDestinationChoice(string choice) {
  if(operation!=null||reportPath==null||workspace==null||currentMovePlan==null||PreviewGrid.SelectedItem is not PreviewRow selected)return;
  try {
   var folder=Path.Combine(outputPath,"move-preview");
   if(PreviewCache.Read(folder,CurrentPreviewSignature())==null)throw new InvalidOperationException("The preview changed. Regenerate it before choosing a destination.");
   operation=new();SetBusy(true);
   var fresh=Data.Read(reportPath);DestinationChoices.Save(fresh,currentMovePlan,selected.Item.Source,choice);
   Data.Save(reportPath,fresh);report=fresh;
   var catalog=string.IsNullOrWhiteSpace(catalogPath)?[]:Data.Catalog(catalogPath);
   var root=LibraryPath.Text;var series=SeriesName.Text;var destination=currentMovePlan.DestinationRoot;
   var plan=await Task.Run(()=>PreviewPlanner.Create(fresh,root,series,catalog,naming:appSettings.Naming,identity:workspace.SeriesIdentity,destinationRoot:destination,contentOptions:workspace.ContentOptions));
   previewHtml=PreviewPlanner.Write(plan,folder);PreviewCache.Save(folder,CurrentPreviewSignature(),plan);DisplayPreview(plan);
   PreviewGrid.SelectedItem=((IEnumerable<PreviewRow>)PreviewGrid.ItemsSource).SingleOrDefault(r=>PreviewPlanner.Same(r.Item.Source,selected.Item.Source));
   RecordEvent("Destination choice saved. Review the preview before applying moves.");
  }catch(Exception ex){Error(ex);}
  finally{operation?.Dispose();operation=null;SetBusy(false);RefreshMoveSelection();}
 }
}
