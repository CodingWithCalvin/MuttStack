using System.Windows;
using Scout.Core;
namespace Scout.Desktop;
public partial class MainWindow {
 private void RefreshOpenSubtitlesStatus() {
  if(OpenSubtitlesKeyStatus==null)return;
  try {var saved=OpenSubtitlesCredentials.Read(settingsStore);OpenSubtitlesKeyStatus.Text=saved==null?"Key not configured":saved.Username.Length==0?"Key configured · API-key access":"Key and account login configured";}
  catch(Exception){OpenSubtitlesKeyStatus.Text="Credentials could not be unlocked. Remove them and enter a replacement.";}
 }
 private void SaveOpenSubtitlesCredentials() {
  OpenSubtitlesCredentials.Save(settingsStore,OpenSubtitlesKeyEntry.Password,OpenSubtitlesUsernameEntry.Text,OpenSubtitlesPasswordEntry.Password);
  OpenSubtitlesKeyEntry.Clear();OpenSubtitlesUsernameEntry.Clear();OpenSubtitlesPasswordEntry.Clear();RefreshOpenSubtitlesStatus();
 }
 private void SaveOpenSubtitlesKey(object sender,RoutedEventArgs e) {
  if(operation!=null)return;
  try{SaveOpenSubtitlesCredentials();SettingsStatus.Text="OpenSubtitles credentials saved. "+OpenSubtitlesKeyStatus.Text+".";}catch(Exception ex){Error(ex);}
 }
 private void RemoveOpenSubtitlesKey(object sender,RoutedEventArgs e) {
  if(operation!=null)return;
  try{settingsStore.SetKey(OpenSubtitlesCredentials.CredentialId,null);OpenSubtitlesKeyEntry.Clear();OpenSubtitlesUsernameEntry.Clear();OpenSubtitlesPasswordEntry.Clear();RefreshOpenSubtitlesStatus();SettingsStatus.Text="OpenSubtitles credentials removed. Saved subtitle references are retained.";}catch(Exception ex){Error(ex);}
 }
 private void RemoveOpenSubtitlesLogin(object sender,RoutedEventArgs e) {
  if(operation!=null)return;
  try{OpenSubtitlesCredentials.RemoveLogin(settingsStore);OpenSubtitlesUsernameEntry.Clear();OpenSubtitlesPasswordEntry.Clear();RefreshOpenSubtitlesStatus();SettingsStatus.Text="OpenSubtitles account login removed; the API key is retained.";}catch(Exception ex){Error(ex);}
 }
}
