using System.Windows;
using Scout.Core;
namespace Scout.Desktop;
public partial class MainWindow {
 private void RefreshSubdlKeyStatus() {
  if(SubdlKeyStatus==null)return;
  try{SubdlKeyStatus.Text=string.IsNullOrWhiteSpace(settingsStore.ReadKey(SubtitleCredentials.Subdl))?"Key not configured":"Key configured";}
  catch(Exception){SubdlKeyStatus.Text="Key could not be unlocked. Enter a replacement.";}
 }
 private void SaveSubtitleKey() {
  SubtitleCredentials.SaveSubdl(settingsStore,SubdlKeyEntry.Password);
  SubdlKeyEntry.Clear();RefreshSubdlKeyStatus();
 }
 private void SaveSubdlKey(object sender,RoutedEventArgs e) {
  if(operation!=null)return;
  try {SaveSubtitleKey();SettingsStatus.Text="SubDL credentials saved. "+SubdlKeyStatus.Text+".";}
  catch(Exception ex){Error(ex);}
 }
 private void RemoveSubdlKey(object sender,RoutedEventArgs e) {
  if(operation!=null)return;
  try {settingsStore.SetKey(SubtitleCredentials.Subdl,null);SubdlKeyEntry.Clear();RefreshSubdlKeyStatus();SettingsStatus.Text="SubDL key removed.";}
  catch(Exception ex){Error(ex);}
 }
}
