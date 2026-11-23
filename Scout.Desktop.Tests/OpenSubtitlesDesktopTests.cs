using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Scout.Core;
using Scout.Desktop;

static class OpenSubtitlesDesktopTests {
 public static void Run(MainWindow window,SettingsStore settings,WorkspaceStore workspaces) {
  void Check(bool ok){if(!ok)throw new Exception("OpenSubtitles settings regression");}
  var before=JsonSerializer.Serialize(workspaces.ReadAll(includeArchived:true),Data.Options);
  var key=(PasswordBox)window.FindName("OpenSubtitlesKeyEntry");var username=(TextBox)window.FindName("OpenSubtitlesUsernameEntry");var password=(PasswordBox)window.FindName("OpenSubtitlesPasswordEntry");var status=(TextBlock)window.FindName("OpenSubtitlesKeyStatus");
  void Click(string name)=>((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
  var subdl=settings.ReadKey(SubtitleCredentials.Subdl);var tmdb=settings.ReadKey("metadata|tmdb");
  key.Password="fake-open-key";Click("OpenSubtitlesSaveButton");Check(key.Password==""&&status.Text=="Key configured · API-key access");
  username.Text="fake-user";password.Password="fake-password";Click("OpenSubtitlesSaveButton");
  Check(username.Text==""&&password.Password==""&&status.Text=="Key and account login configured");
  Click("OpenSubtitlesSaveButton");Check(OpenSubtitlesCredentials.Read(settings)==new OpenSubtitlesCredentials("fake-open-key","fake-user","fake-password"));
  typeof(MainWindow).GetMethod("LoadSettings",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null);
  Check(key.Password==""&&username.Text==""&&password.Password==""&&status.Text=="Key and account login configured");
  Click("OpenSubtitlesRemoveLoginButton");Check(OpenSubtitlesCredentials.Read(settings)==new OpenSubtitlesCredentials("fake-open-key"));
  Click("OpenSubtitlesRemoveButton");Check(OpenSubtitlesCredentials.Read(settings)==null&&status.Text=="Key not configured");
  Check(settings.ReadKey(SubtitleCredentials.Subdl)==subdl&&settings.ReadKey("metadata|tmdb")==tmdb&&before==JsonSerializer.Serialize(workspaces.ReadAll(includeArchived:true),Data.Options));
  Console.WriteLine("PASS OpenSubtitles settings save, retain, reload and remove encrypted credentials independently without changing Scouts.");
 }
}
