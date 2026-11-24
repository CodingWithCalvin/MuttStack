using System.Text.Json;
namespace Scout.Core;

public sealed record OpenSubtitlesCredentials(string ApiKey,string Username="",string Password="") {
 public override string ToString()=>"OpenSubtitles credentials";
 public void Validate() {
  if(string.IsNullOrWhiteSpace(ApiKey)||ApiKey.Length>4096||ApiKey.Any(c=>char.IsWhiteSpace(c)||char.IsControl(c)||c>126))throw new ArgumentException("Enter a valid OpenSubtitles API key.");
  if(Username.Length>128||Username.Any(char.IsControl)||Password.Length>4096||Password.Any(char.IsControl)||
   string.IsNullOrWhiteSpace(Username)!=string.IsNullOrEmpty(Password))throw new ArgumentException("Enter both the OpenSubtitles username and password, or leave both blank for API-key access.");
 }
 public const string CredentialId="subtitles|opensubtitles";
 public static OpenSubtitlesCredentials? Read(SettingsStore store) {
  var saved=store.ReadKey(CredentialId);if(string.IsNullOrEmpty(saved))return null;
  var value=JsonSerializer.Deserialize<OpenSubtitlesCredentials>(saved,Data.Options)??throw new InvalidDataException("OpenSubtitles credentials could not be read.");value.Validate();return value;
 }
 public static void Save(SettingsStore store,string key,string username,string password) {
  if(string.IsNullOrWhiteSpace(key)&&string.IsNullOrWhiteSpace(username)&&password.Length==0)return;
  var old=Read(store);
  var value=new OpenSubtitlesCredentials(string.IsNullOrWhiteSpace(key)?old?.ApiKey??"":key.Trim(),
   string.IsNullOrWhiteSpace(username)?old?.Username??"":username.Trim(),password.Length==0?old?.Password??"":password);
  value.Validate();store.SetKey(CredentialId,JsonSerializer.Serialize(value,Data.Options));
 }
 public static void RemoveLogin(SettingsStore store) {
  var old=Read(store);if(old!=null)store.SetKey(CredentialId,JsonSerializer.Serialize(old with{Username="",Password=""},Data.Options));
 }
}
