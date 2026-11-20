using System.IO;
using System.Reflection;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Tucker;

static class ReprocessDesktopTests
{
    public static async Task<int> Run(string root,string fake,string? renders)
    {
        int passed=0;void Check(bool ok,string label){if(!ok)throw new Exception(label);Console.WriteLine("PASS "+label);passed++;}
        var home=Path.Combine(root,"reprocess-ui");Directory.CreateDirectory(home);
        var raw=Path.Combine(home,"raw","Season 02");Directory.CreateDirectory(raw);
        foreach(var prefix in new[]{"V01D01","V01D02","V02D01","V02D02"})File.WriteAllText(Path.Combine(raw,prefix+"-title.mkv"),"raw "+prefix);
        var preset=Path.Combine(home,"preset.json");File.WriteAllText(preset,"""{"VersionMajor":72,"PresetList":[{"PresetName":"Folder UI","Type":1,"Folder":false,"FileFormat":"av_mkv","VideoEncoder":"x265_10bit"}]}""");
        var dbPath=Path.Combine(home,"doggo.db");var db=new DoggoDatabase(dbPath);
        db.Settings(new Preferences(fake,preset,"",Path.Combine(home,"out"),"{disc_label}","","Folder UI",1));
        var w=new MainWindow(home,dbPath);
        T C<T>(string name)=>(T)w.FindName(name);
        void Click(string name)=>C<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        async Task Preview()=>await (Task)typeof(MainWindow).GetMethod("PreparePreview",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(w,[])!;
        List<Job> Rows()=>C<DataGrid>("PreviewGrid").Items.Cast<Job>().ToList();
        void SelectMissing()
        {
            var grid=C<DataGrid>("PreviewGrid");grid.SelectedItems.Clear();
            foreach(var job in Rows().Where(j=>j.OriginalFilename.StartsWith("V01D02")||j.OriginalFilename.StartsWith("V02D02")))grid.SelectedItems.Add(job);
        }
        try {
            ((ObservableCollection<string>)typeof(MainWindow).GetField("folders",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(w)!).Add(raw);
            await Preview();var plan=Rows();var old=w.Manager.AddRun(plan,false,[],"Old deleted run");w.Manager.DeleteRun(old.Id);
            var first=plan.Single(j=>j.OriginalFilename.StartsWith("V01D01"));first.Reprocess=true;
            var pending=w.Manager.AddRun([first],false,[],"Disc 1 waiting");await Preview();
            Check(Rows().Count(j=>j.Status=="Already queued")==1&&Rows().Count(j=>j.Status=="Already imported")==3&&C<TextBlock>("PreviewSummary").Text.Contains("not necessarily compressed"),"Folder preview distinguishes queued files from import history without claiming compression completed");
            Check(!C<Button>("ReprocessSelectedButton").IsEnabled,"Folder reprocessing requires an explicit file selection");
            SelectMissing();Check(C<Button>("ReprocessSelectedButton").IsEnabled,"Selecting historical folder files enables Reprocess selected");
            Click("ReprocessSelectedButton");
            Check(Rows().Count(j=>j.Status=="Ready to reprocess")==2&&Rows().Count(j=>j.Status=="Already imported")==1&&Rows().Count(j=>j.Status=="Already queued")==1&&C<TextBlock>("Heading").Text.Contains("2 files"),"Reprocess selected updates eligibility and the suggested run count for only the chosen files");
            Check(w.Manager.State.Jobs.Count==1&&!w.Manager.Running&&C<Button>("ResetReprocessingButton").Visibility==Visibility.Visible,"Selecting reprocessing leaves the saved queue untouched until Create Tucker");
            if(renders!=null)await Render(w,Path.Combine(renders,"folder-reprocessing-minimum.png"));
            Click("ResetReprocessingButton");
            Check(Rows().All(j=>!j.Reprocess)&&Rows().Count(j=>j.Status=="Already imported")==3&&C<Button>("ResetReprocessingButton").Visibility==Visibility.Collapsed,"Reset reprocessing restores normal import protections without losing the preview");
            SelectMissing();Click("ReprocessSelectedButton");await Preview();
            Check(Rows().All(j=>!j.Reprocess)&&Rows().Count(j=>j.Status=="Already imported")==3,"A new preview does not carry old per-file reprocessing permission forward");
            SelectMissing();Click("ReprocessSelectedButton");Click("AddWaiting");
            Check(w.Manager.State.Jobs.Count==3&&w.Manager.State.Jobs.Count(j=>j.QueueId==pending.Id)==1&&w.Manager.State.Jobs.All(j=>!j.Reprocess&&j.Status=="Waiting")&&db.Read().Receipts.Count==0,"Saving selected folder retries adds only missing files and preserves the existing waiting run without fabricating inbox receipts");
            w.Close();w=new MainWindow(home,dbPath);
            Check(w.Manager.State.Jobs.Count==3&&!w.Manager.Running&&w.Manager.State.Jobs.All(j=>!j.Reprocess),"Folder retries survive reopen as ordinary waiting jobs with no persistent reprocessing override");
        }finally{if(w.Manager.Running){w.Manager.Stop();await w.Manager.RunTask!;}w.Close();}
        return passed;
    }
    static async Task Render(MainWindow w,string path)
    {
        w.ApplyTemplate();var root=(FrameworkElement)w.Content;root.Measure(new Size(1080,740));root.Arrange(new Rect(0,0,1080,740));root.UpdateLayout();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);root.UpdateLayout();
        var bitmap=new RenderTargetBitmap(1080,740,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);
    }
}
