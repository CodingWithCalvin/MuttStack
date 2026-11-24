using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Scout.Core;
namespace Scout.Desktop;

public sealed class ForceAssignmentWindow:Window
{
    private sealed record Choice(Episode Episode) {public string Label=>$"{Episode.Code} · {Episode.Title}";}
    private sealed record Recording(JsonObject Row) {public string Label=>Path.GetFileName(Row.Text("path"));}
    public JsonObject? SelectedRow {get;private set;}
    public Episode? SelectedEpisode=>SelectedEpisodes.FirstOrDefault();
    public List<Episode> SelectedEpisodes {get;private set;}=[];
    public bool ReviewSubtitleReference {get;private set;}
    public string Note {get;private set;}="";
    public ForceAssignmentWindow(JsonObject report,RecordingGroup group,IReadOnlyList<Episode> catalog) {
        Title="Force episode assignment";Width=820;Height=760;MinWidth=560;MinHeight=560;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;Background=(Brush)Application.Current.FindResource("CanvasBrush");Foreground=Brushes.White;
        var grid=new Grid{Margin=new Thickness(24)};
        foreach(var h in new[]{GridLength.Auto,GridLength.Auto,GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),new GridLength(1,GridUnitType.Star),GridLength.Auto,GridLength.Auto})grid.RowDefinitions.Add(new(){Height=h});
        var heading=new TextBlock{Text="Assign this recording yourself",FontSize=24,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,12)};
        var explanation=new TextBlock{Text="Choose one complete episode, or up to four consecutive complete episodes from the saved guide (Ctrl/Shift to select a range). Your assignment takes precedence over model suggestions and earlier overrides. Other recordings claiming it stay held. The model evidence is kept, and you can undo this decision. No files are moved here.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)};
        var recordings=new ListBox{ItemsSource=group.Rows.Select(r=>new Recording(r)).ToList(),DisplayMemberPath="Label",MaxHeight=95,Margin=new Thickness(0,0,0,8)};
        var search=new TextBox{Margin=new Thickness(0,0,0,8),ToolTip="Search season, episode number or title"};
        System.Windows.Automation.AutomationProperties.SetName(search,"Search episode guide");
        var choices=new ListBox{SelectionMode=SelectionMode.Extended,DisplayMemberPath="Label",Margin=new Thickness(0,0,0,8)};
        var details=new TextBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(0,0,0,12)};
        var notePanel=new StackPanel();
        var referenceReview=new CheckBox{Content="I verified the matched subtitle reference covers this full episode selection",IsChecked=false,Margin=new Thickness(0,0,0,8),ToolTip="Records a reviewed provider mapping for this exact reference in this Scout. Reanalysis still requires full dialogue correspondence. Undoing the assignment also disables this mapping."};
        notePanel.Children.Add(referenceReview);notePanel.Children.Add(new TextBlock{Text="Reason (optional)"});
        var note=new TextBox{MaxLength=500,Margin=new Thickness(0,6,0,12)};notePanel.Children.Add(note);
        var buttons=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right};
        var cancel=new Button{Content="Cancel",IsCancel=true};var confirm=new Button{Content="Force assignment",Style=(Style)Application.Current.FindResource("Primary")};
        buttons.Children.Add(cancel);buttons.Children.Add(confirm);
        var groups=CollectionResolver.Resolve(report);
        void Refresh() {
            SelectedRow=(recordings.SelectedItem as Recording)?.Row;SelectedEpisodes=choices.SelectedItems.Cast<Choice>().Select(c=>c.Episode).OrderBy(e=>e.Season).ThenBy(e=>e.Number).ToList();
            confirm.IsEnabled=SelectedRow!=null&&EpisodeRange.Valid(SelectedEpisodes);
            referenceReview.IsEnabled=SelectedRow!=null&&Scout.Core.Subtitles.ReviewedReferenceMapping.CanReview(SelectedRow);
            if(!referenceReview.IsEnabled)referenceReview.IsChecked=false;
            if(SelectedRow==null||!EpisodeRange.Valid(SelectedEpisodes)){details.Text="Select a recording and one to four consecutive episodes in the same season to see the effect of your decision.";return;}
            var fingerprint=CollectionResolver.FingerprintKey(SelectedRow["primary_fingerprint"]);
            var affected=groups.Where(g=>g.Claims.Any(e=>SelectedEpisodes.Any(selected=>selected.Id==e.Id))).SelectMany(g=>g.Rows)
                .Where(r=>CollectionResolver.FingerprintKey(r["primary_fingerprint"])!=fingerprint).Select(r=>r.Text("path")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            details.Text=SelectedRow.Text("path")+"\n\n"+string.Join("\n",SelectedEpisodes.Select(e=>e.Code+" · "+e.Title+"\n"+e.Overview))+
                "\n\n"+(affected.Count==0?"No other recordings claim this episode.":$"{affected.Count} competing recording(s) will remain held:\n"+string.Join("\n",affected))+
                "\n\nExact fingerprint copies share this assignment. Source and destination safety checks still apply. Undo changes the assignment, not any moves already completed.";
        }
        void Filter() {
            var previous=(choices.SelectedItem as Choice)?.Episode;
            choices.ItemsSource=catalog.OrderBy(e=>e.Season).ThenBy(e=>e.Number).Select(e=>new Choice(e)).Where(c=>c.Label.Contains(search.Text.Trim(),StringComparison.OrdinalIgnoreCase)).ToList();
            if(previous!=null)choices.SelectedItem=((IEnumerable<Choice>)choices.ItemsSource).FirstOrDefault(c=>c.Episode.Id==previous.Id);
            Refresh();
        }
        search.TextChanged+=(_,_)=>Filter();choices.SelectionChanged+=(_,_)=>Refresh();recordings.SelectionChanged+=(_,_)=>Refresh();
        confirm.Click+=(_,_)=>{if(SelectedRow!=null&&SelectedEpisode!=null){Note=note.Text.Trim();ReviewSubtitleReference=referenceReview.IsChecked==true;DialogResult=true;}};
        foreach(var (control,row) in new (UIElement,int)[]{(heading,0),(explanation,1),(recordings,2),(search,3),(choices,4),(details,5),(notePanel,6),(buttons,7)}){Grid.SetRow(control,row);grid.Children.Add(control);}
        Content=grid;Filter();recordings.SelectedIndex=0;
        var suggested=group.Episode?.Id??group.Rows[0].Array("candidates").FirstOrDefault()?["episode"].Int("id");
        choices.SelectedItem=((IEnumerable<Choice>)choices.ItemsSource).FirstOrDefault(c=>c.Episode.Id==suggested);Refresh();
    }
}
