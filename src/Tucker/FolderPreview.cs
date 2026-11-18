using System.IO;
using System.Windows;
using System.Windows.Controls;
namespace Tucker;

public sealed record FolderExample(string Id,string Display,FolderContext Context,bool IsExample=false);

public partial class MainWindow
{
    Job[] folderExampleJobs=[];
    bool refreshingFolderExample;
    static FolderExample ExampleFor(Job job)=>new(job.Source,$"{job.DiscLabel} · {Path.GetFileName(job.Source)}",new(job.Series,job.Season,job.DiscLabel,job.RipDate,job.VolumeNumber,job.DiscNumber));
    void RefreshFolderExamples(bool preferSelected=false)
    {
        var draft=folders.Count>0||stagedBatches.Count>0||appendRunId!=null;
        var examples=folderExampleJobs.Length>0
            ?folderExampleJobs.Select(ExampleFor).ToList()
            :stagedBatches.SelectMany(b=>b.Manifest.Files).Select(f=>new FolderExample(f.Source,$"{f.DiscLabel} · {Path.GetFileName(f.Source)}",new(f.Series,f.Season,f.DiscLabel,f.RipDate,f.VolumeNumber,f.DiscNumber))).ToList();
        if(!draft)examples=SelectedJobs().Select(ExampleFor).ToList();
        examples=examples.DistinctBy(f=>f.Id,StringComparer.OrdinalIgnoreCase).ToList();
        if(examples.Count==0)examples.Add(new("example","Example metadata",new(new("tvdb","123456","Example Show","2024",""),"1","Example Show S1 V1 D2",new DateTime(2024,1,15),"1","2"),true));
        var selectedId=(FolderExampleSource.SelectedItem as FolderExample)?.Id;
        var selectedFile=(draft?PreviewGrid.SelectedItem:QueueGrid.SelectedItem) as Job;
        var selected=preferSelected?examples.FirstOrDefault(f=>f.Id==selectedFile?.Source):null;
        selected??=examples.FirstOrDefault(f=>f.Id==selectedId)??examples[0];
        refreshingFolderExample=true;
        try{FolderExampleSource.ItemsSource=examples;FolderExampleSource.SelectedItem=selected;FolderExampleSource.IsEnabled=examples.Count>1;}
        finally{refreshingFolderExample=false;}
        FolderExampleNote.Text=selected.IsExample
            ?"Example values. Preview existing rips or add inbox batches to use their metadata."
            :draft?"Each source uses its own metadata. Preview the run again after changing its output settings."
            :"Current settings applied to this source. Saved runs keep their existing output paths.";
        UpdateFolderExample();
    }
    void FolderExampleChanged(object sender,SelectionChangedEventArgs e){if(!refreshingFolderExample)UpdateFolderExample();}
    void FolderFormatChanged(object sender,TextChangedEventArgs e)=>UpdateFolderExample();
    void UpdateFolderExample()
    {
        if(FolderExampleSource.SelectedItem is not FolderExample sample)return;
        static string Value(string? value)=>string.IsNullOrWhiteSpace(value)?"—":value;
        var c=sample.Context;
        FolderExampleSource.ToolTip=sample.IsExample?"Illustrative metadata only":sample.Id;
        FolderExampleMetadata.Text=$"Series: {Value(c.Series?.Name)} · Year: {Value(c.Series?.Year)} · TVDB: {Value(c.Series?.Provider=="tvdb"?c.Series.Id:null)}\nSeason: {Value(c.Season)} · Volume: {Value(c.Volume)} · Disc: {Value(c.DiscNumber)} · Date: {c.Date:yyyy-MM-dd}";
        try{
            // Use the production formatter without filesystem checks while the user types.
            FolderExamplePath.Text=FolderNames.Resolve(Destination.Text.Trim(),OutputFormat.Text,c,checkLinks:false);
            FolderExamplePath.Visibility=Visibility.Visible;FolderExampleError.Visibility=Visibility.Collapsed;FolderExampleError.Text="";
        }catch(Exception ex) when(ex is IOException or ArgumentException or NotSupportedException){
            FolderExamplePath.Text="";FolderExamplePath.Visibility=Visibility.Collapsed;FolderExampleError.Visibility=Visibility.Visible;
            var message=ex.Message;
            if(message.StartsWith("Missing value for ",StringComparison.Ordinal))message=message[..(message.IndexOf('.')+1)]+" This source has no saved value. Use its saved metadata or a format without that token.";
            FolderExampleError.Text=message;
        }
    }
}
