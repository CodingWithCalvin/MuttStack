using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using Scout.Core;
using Scout.Desktop;

static class DialogueAvailabilityDesktopTests {
    public static void Run(MainWindow window,WorkspaceStore store) {
        const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
        void Set(string name,object? value)=>typeof(MainWindow).GetField(name,flags)!.SetValue(window,value);
        T C<T>(string name)=>(T)window.FindName(name);
        void Check(bool yes,string message){if(!yes)throw new Exception(message);Console.WriteLine("PASS "+message);}
        var root=Path.Combine(store.Root,"dialogue-ui-input");Directory.CreateDirectory(root);var run=store.CreateRun(root);
        var reportPath=Path.Combine(store.DirectoryFor(run),"report.json");
        var report=new JsonObject{["files"]=new JsonArray(
            new JsonObject{["path"]=Path.Combine(root,"old.mkv"),["error"]="No readable text subtitles. Speech transcription is not yet available in the .NET build.",["candidates"]=new JsonArray()},
            new JsonObject{["path"]=Path.Combine(root,"new.mkv"),["status"]="not_processed",["skip_reason"]="empty_subtitles",["candidates"]=new JsonArray()},
            new JsonObject{["path"]=Path.Combine(root,"broken.mkv"),["error"]="ffprobe failed",["candidates"]=new JsonArray()})};
        Data.Save(reportPath,report);var before=ScoutStorage.ReadText(reportPath);
        Set("workspace",run);Set("openingWorkspace",true);try{window.LoadReport(reportPath);}finally{Set("openingWorkspace",false);}
        C<TextBox>("Search").Text="";C<ComboBox>("Filter").SelectedIndex=0;
        Check(C<TextBlock>("ReviewCount").Text=="1"&&C<TextBlock>("ResultNote").Text.Contains("2 not processed"),"Unreadable dialogue is counted separately from processing errors");
        C<ComboBox>("Filter").SelectedIndex=6;
        Check(C<DataGrid>("ResultsGrid").Items.Count==2&&C<DataGrid>("ResultsGrid").Items.Cast<RecordingGroup>().All(g=>g.DisplayStatus=="Not processed"&&!g.Detail.Contains("Speech transcription")),"Not processed filter shows neutral results for new and older reports");
        C<ComboBox>("Filter").SelectedIndex=4;
        Check(C<DataGrid>("ResultsGrid").Items.Count==1&&ScoutStorage.ReadText(reportPath)==before,"Unresolved filter retains genuine errors without rewriting report evidence");
        C<ComboBox>("Filter").SelectedIndex=0;
    }
}
