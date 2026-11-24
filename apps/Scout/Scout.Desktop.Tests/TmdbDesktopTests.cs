using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using Scout.Core;
using Scout.Core.Subtitles;
using Scout.Desktop;

static class TmdbDesktopTests
{
    public static void Run(string root)
    {
        var series=new SeriesIdentity("tmdb","5371","iCarly","2007");
        var catalog=Enumerable.Range(1,5).Select(n=>new Episode(n,4,10+n,"Story ("+n+")")).ToArray();
        var cues=Enumerable.Range(0,150).Select(n=>new Cue(n*8,$"unique{n} dialogue{n} word{n} reference{n} story{n} line{n}")).ToArray();
        var reference=new SubtitleReference("fixture","iCarly.S04E11E12E13.WEBRip.srt","https://dl.subdl.com/fixture.zip","fixture",4,cues.ToList());
        var matcher=new SubtitleMatcher(new Source(new(series,[reference])),Path.Combine(root,"tmdb-ui-cache"));
        var attempt=matcher.IdentifyAsync(cues,catalog,series,new SilentProgress(),default).GetAwaiter().GetResult();
        var row=SubtitleResult.Row(cues,catalog,attempt);row["path"]=Path.Combine(root,"synthetic.mkv");
        var report=new JsonObject{["series_identity"]=JsonSerializer.SerializeToNode(series,Data.Options),["files"]=new JsonArray(row)};
        var group=new RecordingGroup{Id="fixture",Rows=[row]};
        var window=new ForceAssignmentWindow(report,group,catalog);
        try {
            var grid=(Grid)window.Content;
            var list=grid.Children.OfType<ListBox>().Single(l=>l.SelectionMode==SelectionMode.Extended);
            var check=grid.Children.OfType<StackPanel>().Single().Children.OfType<CheckBox>().Single();
            var confirm=grid.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().Single(b=>b.Content.ToString()=="Force assignment");
            void Assert(bool value,string message){if(!value)throw new Exception(message);}
            Assert(check.IsEnabled&&check.IsChecked==false,"A strong unnamed reference requires opt-in review.");
            foreach(var item in list.Items.Cast<object>().Take(3))list.SelectedItems.Add(item);
            Assert(confirm.IsEnabled&&window.SelectedEpisodes.Count==3,"Three consecutive episodes must be selectable.");
            list.SelectedItems.Add(list.Items[3]);
            Assert(confirm.IsEnabled&&window.SelectedEpisodes.Count==4,"Four consecutive episodes must be selectable.");
            list.SelectedItems.Add(list.Items[4]);Assert(!confirm.IsEnabled,"More than four must remain disabled.");
            list.SelectedItems.Clear();list.SelectedItems.Add(list.Items[0]);list.SelectedItems.Add(list.Items[2]);
            Assert(!confirm.IsEnabled,"A gap in episode selection must remain disabled.");
            row.Remove("subtitle_match");list.SelectedItems.Clear();list.SelectedItems.Add(list.Items[0]);
            Assert(!check.IsEnabled&&check.IsChecked==false,"Missing proof must disable reference review.");
            Console.WriteLine("PASS TMDB multipart selection allows 1–4 consecutive episodes and requires explicit reference review.");
        }finally{window.Close();}
    }
    sealed class Source(SubtitleReferenceSet set):ISubtitleReferenceSource {public Task<SubtitleReferenceSet> LoadAsync(SeriesIdentity series,IReadOnlyList<Episode> catalog,IProgress<string> progress,CancellationToken ct)=>Task.FromResult(set);}
    sealed class SilentProgress:IProgress<string>{public void Report(string value){}}
}
