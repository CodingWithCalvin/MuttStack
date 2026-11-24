using System.IO;
namespace Tucker;
public static class Naming
{
 public static void Apply(List<Job> jobs,string template) {
  if(template=="{filename}")return;
  var reserved=new Dictionary<string,HashSet<string>>(StringComparer.OrdinalIgnoreCase);
  foreach(var job in jobs) {
   var context=new FolderContext(job.Series,job.Season,job.DiscLabel,job.RipDate,job.VolumeNumber,job.DiscNumber);
   // One pass preserves literal braces and never interprets the original filename as a template.
   var stem=System.Text.RegularExpressions.Regex.Replace(template,@"\{\{|\}\}|\{[^{}]*\}|[{}]",m=>m.Value=="{filename}"?Path.GetFileNameWithoutExtension(job.Name):FolderNames.ExpandTokens(m.Value,context,"filename"));
   FileNames.Validate(stem);if(stem.Length>220)throw new IOException("Filename format is too long.");
   var output=job.Encoding!.Output;
   if(!reserved.TryGetValue(output,out var names))reserved[output]=names=new(StringComparer.OrdinalIgnoreCase);
   job.Name=Engine.Unique(output,stem+job.Encoding.Extension,names);
  }
 }
}
