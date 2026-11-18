using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Text.Json;
using Tucker;
static class FolderPreviewDesktopTests
{
    public static async Task<int> Run(string root,string fake,string? renders)
    {
        int passed=0;void Check(bool ok,string label){if(!ok)throw new Exception(label);Console.WriteLine("PASS "+label);passed++;}
        var home=Path.Combine(root,"folder-preview-ui");Directory.CreateDirectory(home);var dbPath=Path.Combine(home,"doggo.db");
        var preset=Path.Combine(home,"preset.json");File.WriteAllText(preset,"""{"VersionMajor":72,"PresetList":[{"PresetName":"Folder preview","Type":1,"Folder":false,"FileFormat":"av_mkv","VideoEncoder":"x265_10bit"}]}""");
        var output=Path.Combine(home,"compressed");var db=new DoggoDatabase(dbPath);var preferences=new Preferences(fake,preset,"",output,@"{series} ({year})\Season {season:00}","","Folder preview",1);db.Settings(preferences);
        var w=new MainWindow(home,dbPath);
        T C<T>(string name)=>(T)w.FindName(name);
        void Click(string name)=>C<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        object? Call(string name,params object?[] args)=>typeof(MainWindow).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(w,args);
        async Task Preview()=>await (Task)Call("PreparePreview")!;
        string PathText()=>C<TextBox>("FolderExamplePath").Text;
        Handoff Offer(string label,string? series,string season,string volume,string disc){
            var dir=Path.Combine(home,"raw",label);Directory.CreateDirectory(dir);var file=Path.Combine(dir,"title.mkv");File.WriteAllText(file,"raw");var f=new FileInfo(file);
            var manifest=new HandoffManifest{Id=Guid.NewGuid().ToString("D"),Files=[new(){Source=file,SourceSize=f.Length,SourceModified=f.LastWriteTimeUtc,Series=series==null?null:new("tvdb","123",series,"2020",""),Season=season,VolumeNumber=volume,DiscNumber=disc,DiscLabel=label,DiscId=label,RipDate=new DateTime(2026,1,2)}]};
            var path=Path.Combine(home,manifest.Id+".json");QueueStore.Write(path,manifest);var h=Handoff.Read(path);db.Offer(h);return h;
        }
        void Use(params Handoff[] batches){Click("NavInbox");C<DataGrid>("InboxGrid").SelectedItems.Clear();foreach(var item in C<DataGrid>("InboxGrid").Items.Cast<InboxItem>().Where(i=>batches.Any(b=>b.Manifest.Id==i.Id)))C<DataGrid>("InboxGrid").SelectedItems.Add(item);Click("UseInboxBatches");}
        void Choose(Handoff batch)=>C<ComboBox>("FolderExampleSource").SelectedItem=C<ComboBox>("FolderExampleSource").Items.Cast<FolderExample>().Single(f=>f.Id==batch.Manifest.Files[0].Source);
        try{
            Click("NavSettings");
            Check(C<ComboBox>("FolderExampleSource").SelectedItem is FolderExample {IsExample:true}&&PathText()==Path.Combine(output,@"Example Show (2024)\Season 01")&&C<TextBlock>("FolderExampleNote").Text.StartsWith("Example values"),"Settings shows a clearly labeled full example folder before sources are selected");
            C<TextBox>("OutputFormat").Text=@"{series} {{tvdb-{tvdb_id}}}\Season {season:00}\Volume {volume_number:00}\Disc {disc_number:00}\{date:yyyyMMdd}";
            Check(PathText()==Path.Combine(output,@"Example Show {tvdb-123456}\Season 01\Volume 01\Disc 02\20240115"),"Folder example updates immediately for nested folders, padding, escaped braces and dates");
            C<TextBox>("Destination").Text=@"\\media-server\compressed";
            Check(PathText()==@"\\media-server\compressed\Example Show {tvdb-123456}\Season 01\Volume 01\Disc 02\20240115","Changing the base folder immediately updates the full path without accessing the share");
            C<TextBox>("OutputFormat").Text="{unknown}";
            Check(PathText()==""&&C<TextBlock>("FolderExampleError").Text.Contains("Unknown folder token")&&C<TextBlock>("FolderExampleError").Visibility==Visibility.Visible,"Invalid tokens replace stale output with an inline explanation");
            C<TextBox>("OutputFormat").Text=@"..\outside";Check(PathText()==""&&C<TextBlock>("FolderExampleError").Text.Contains("cannot contain"),"Folder example rejects paths that escape the output base");
            C<TextBox>("Destination").Text="relative";C<TextBox>("OutputFormat").Text="{disc_label}";
            Check(PathText()==""&&C<TextBlock>("FolderExampleError").Text.Contains("absolute base folder"),"A missing or relative base folder is explained inline");
            C<TextBox>("Destination").Text=output;C<TextBox>("OutputFormat").Text=@"{series} {{tvdb-{tvdb_id}}}\Season {season:00}\{disc_label}";
            Check(PathText().Length>0&&C<TextBlock>("FolderExampleError").Visibility==Visibility.Collapsed&&!Directory.Exists(output)&&db.Settings<Preferences>()==preferences&&w.Manager.State.Jobs.Count==0,"Live preview recovers on correction without creating folders, saving preferences or creating a run");
            if(renders!=null){Directory.CreateDirectory(renders);await Render(w,1080,740,Path.Combine(renders,"folder-settings-example-minimum.png"));}
            var a=Offer("Alpha S2 V3 D4","Actual: Show","2","3","4");var b=Offer("Beta S5 V2 D1","Other Show","5","2","1");var missing=Offer("No metadata",null,"","","");
            Use(a,b,missing);Click("NavSettings");Choose(a);
            Check(C<ComboBox>("FolderExampleSource").Items.Count==3&&PathText()==Path.Combine(output,@"Actual_ Show {tvdb-123}\Season 02\Alpha S2 V3 D4")&&C<TextBlock>("FolderExampleMetadata").Text.Contains("Volume: 3"),"Staged inbox files supply real metadata and the same filename sanitization as compression");
            Choose(b);Check(PathText()==Path.Combine(output,@"Other Show {tvdb-123}\Season 05\Beta S5 V2 D1"),"The source selector previews a different batch's exact folder");
            Choose(missing);Check(PathText()==""&&C<TextBlock>("FolderExampleError").Text.Contains("Missing value for {series}")&&C<ComboBox>("FolderExampleSource").SelectedItem is FolderExample {IsExample:false},"Incomplete real metadata produces an error without substituting example values");
            C<ListBox>("SourcesList").SelectedItem=C<ListBox>("SourcesList").Items.Cast<DraftSource>().Single(s=>s.Id==missing.Manifest.Id);Call("RemoveSources",w,new RoutedEventArgs());
            await Preview();var jobs=C<DataGrid>("PreviewGrid").Items.Cast<Job>().ToList();var chosen=jobs.Single(j=>j.Source==a.Manifest.Files[0].Source);C<DataGrid>("PreviewGrid").SelectedItem=chosen;Click("NavSettings");
            Check(PathText()==chosen.Encoding!.Output&&((FolderExample)C<ComboBox>("FolderExampleSource").SelectedItem).Id==chosen.Source,"Settings uses the selected preview file and matches its planned output folder exactly");
            if(renders!=null)await Render(w,1440,920,Path.Combine(renders,"folder-settings-real.png"));
            C<TextBox>("OutputFormat").Text="{unknown}";await Preview();Click("NavSettings");
            Check(C<ComboBox>("FolderExampleSource").Items.Count==2&&C<ComboBox>("FolderExampleSource").SelectedItem is FolderExample {IsExample:false}&&PathText()=="","A failed re-preview retains real source metadata for fixing the folder format");
            C<TextBox>("OutputFormat").Text=@"{series}\Season {season:00}";await Preview();Click("AddWaiting");var frozen=JsonSerializer.Serialize(w.Manager.State.Jobs);Click("NavSettings");
            var newOutput=Path.Combine(home,"new-compressed");C<TextBox>("Destination").Text=newOutput;
            Check(PathText().StartsWith(newOutput)&&C<TextBlock>("FolderExampleNote").Text.Contains("Saved runs keep")&&JsonSerializer.Serialize(w.Manager.State.Jobs)==frozen&&!Directory.Exists(newOutput),"Saved-run metadata previews current preferences without changing any frozen job settings");
            Click("NewTuckerButton");Click("NavSettings");C<ComboBox>("PresetChoice").SelectedIndex=-1;
            Check(C<ComboBox>("FolderExampleSource").SelectedItem is FolderExample {IsExample:true}&&PathText().Contains("Example Show"),"A fresh draft returns to labeled example values and folder preview works without a chosen preset");
        }finally{w.Close();}
        return passed;
    }
    static async Task Render(MainWindow w,int width,int height,string path){w.ApplyTemplate();var root=(FrameworkElement)w.Content;root.Measure(new Size(width,height));root.Arrange(new Rect(0,0,width,height));root.UpdateLayout();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);root.UpdateLayout();var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);}
}
