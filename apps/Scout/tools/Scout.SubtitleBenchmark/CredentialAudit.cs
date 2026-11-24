using System.Text;
using Scout.Core;

static class CredentialAudit {
 public static void Run(string output,string database,string settings) {
  var key=SubtitleCredentials.ReadSubdl(database,settings)??throw new InvalidOperationException("No saved SubDL key is configured.");
  var keys=new[]{key,SubtitleCredentials.ReadMetadata(database,settings,"tvdb"),SubtitleCredentials.ReadMetadata(database,settings,"tvdb-pin"),SubtitleCredentials.ReadMetadata(database,settings,"tmdb")}.Where(k=>!string.IsNullOrEmpty(k)).Select(k=>k!).ToArray();
  var needles=keys.SelectMany(k=>new[]{Encoding.UTF8.GetBytes(k),Encoding.UTF8.GetBytes(Uri.EscapeDataString(k))}).ToArray();
  var files=Directory.EnumerateFiles(Path.GetFullPath(output),"*",SearchOption.TopDirectoryOnly).ToArray();
  foreach(var file in files) {
   var bytes=File.ReadAllBytes(file);
   if(needles.Any(n=>bytes.AsSpan().IndexOf(n)>=0))throw new InvalidDataException("A credential appeared in a test artifact. Do not share this output.");
  }
  Console.WriteLine($"Credential audit passed: {files.Length} output files checked; saved subtitle/metadata credentials not present. Credential values were not printed.");
 }
}
