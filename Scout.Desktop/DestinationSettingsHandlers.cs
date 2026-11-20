using System.IO;
using System.Windows;
using Scout.Core;
namespace Scout.Desktop;

public partial class MainWindow {
    private void GlobalDestinationEdited(object? sender,EventArgs e)=>RefreshGlobalDestinationExample();
    private void BrowseGlobalDestination(object sender,RoutedEventArgs e) {
        if(operation!=null)return;
        if(PickFolder("Choose a base folder for all shows") is string folder)
            DefaultDestinationTemplate.Text=Path.Combine(DestinationTemplate.Literal(folder),"{series}");
    }
    private void OpenDestinationSettings(object sender,RoutedEventArgs e) {ShowPage("Settings");SettingsTabs.SelectedIndex=0;DefaultDestinationTemplate.BringIntoView();DefaultDestinationTemplate.Focus();}
    private static string DestinationEpisodeExample(string root,NamingOptions naming,string series,SeriesIdentity? identity) {
        var episode=new Episode(1,1,1,"Opening Night");naming.Validate();
        return Path.Combine(root,naming.Folder(series,episode,identity),naming.FileName(series,episode,identity)+".mkv");
    }
    private void RefreshGlobalDestinationExample() {
        if(DefaultDestinationExample==null||DefaultDestinationTemplate==null||DefaultPathTemplate==null)return;
        try {
            var identity=workspace?.SeriesIdentity??new SeriesIdentity("tvdb","12345","Example Show","2020");
            var destination=DestinationTemplate.Resolve(DefaultDestinationTemplate.Text,identity.Name,identity);
            var root=destination??workspace?.Library??@"C:\Source\Example Show";
            DefaultDestinationExample.Text=(workspace?.SeriesIdentity==null?"Example show · ":"Example for "+identity.Name+" · ")+
                (destination==null?"Organize within each Scout's source folder":"Destination: "+destination)+
                "\nExample episode: "+DestinationEpisodeExample(root,DefaultNaming(),identity.Name,identity);
            DefaultDestinationExample.Foreground=(System.Windows.Media.Brush)FindResource("AccentBrush");
        } catch(ArgumentException ex) {DefaultDestinationExample.Text=ex.Message;DefaultDestinationExample.Foreground=System.Windows.Media.Brushes.Salmon;}
    }
}
