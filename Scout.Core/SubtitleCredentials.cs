using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.Json;
namespace Scout.Core;

public static class SubtitleCredentials {
 public const string Subdl="subtitles|subdl";
 public static void SaveSubdl(SettingsStore store,string value) {
  if(string.IsNullOrWhiteSpace(value))return;
  var key=value.Trim();
  if(key.Length>4096||key.Any(char.IsControl))throw new ArgumentException("Enter a single SubDL API key.");
  store.SetKey(Subdl,key);
 }
 // Isolated diagnostics read only the protected credential document. They must
 // not initialize/migrate a live database or open/update any Scout results.
 public static string? ReadSubdl(string database,string settingsDirectory)=>Read(database,settingsDirectory,Subdl);
 public static string? ReadMetadata(string database,string settingsDirectory,string provider) {
  if(provider is not ("tvdb" or "tmdb" or "tvdb-pin"))throw new ArgumentException("Unknown metadata credential.");
  return Read(database,settingsDirectory,"metadata|"+provider);
 }
 static string? Read(string database,string settingsDirectory,string credentialId) {
  if(!File.Exists(database))return null;
  using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.GetFullPath(database),Mode=SqliteOpenMode.ReadOnly,Pooling=false,DefaultTimeout=5}.ToString());db.Open();
  using var cmd=db.CreateCommand();cmd.CommandText="SELECT name FROM sqlite_master WHERE type='table' AND name IN ('documents','scout_documents')";
  var tables=new List<string>();using(var reader=cmd.ExecuteReader())while(reader.Read())tables.Add(reader.GetString(0));
  if(tables.Count!=1)throw new InvalidDataException("Cannot identify the Scout credential table.");
  cmd.CommandText=$"SELECT content FROM {tables[0]} WHERE path=$path COLLATE NOCASE";
  cmd.Parameters.AddWithValue("$path",Path.Combine(Path.GetFullPath(settingsDirectory),"credentials.dpapi"));
  if(cmd.ExecuteScalar() is not byte[] encrypted)return null;
  var plain=WindowsSecrets.Unprotect(encrypted);
  try{return JsonSerializer.Deserialize<Dictionary<string,string>>(plain)?.GetValueOrDefault(credentialId);}
  finally{CryptographicOperations.ZeroMemory(plain);}
 }
}
