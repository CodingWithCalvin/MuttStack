using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;

namespace Scout.Core.Subtitles;

// Successful searches are reusable across runs. Errors never become empty results.
internal static class SubtitleSearchCache {
 static readonly ConcurrentDictionary<string,SemaphoreSlim> Gates=new(StringComparer.OrdinalIgnoreCase);
 public static async Task<JsonObject> Get(string root,string key,Func<Task<JsonObject>> fetch,Func<JsonObject,bool> valid,IProgress<string> progress,CancellationToken ct) {
  ct.ThrowIfCancellationRequested();var path=Path.Combine(root,"searches-v1",CollectionResolver.Hash(key)+".json");
  var gate=Gates.GetOrAdd(Path.GetFullPath(path),_=>new(1,1));await gate.WaitAsync(ct);
  try{return await Load();}finally{gate.Release();}
  async Task<JsonObject> Load() {
  if(ScoutStorage.Exists(path))try {
   var saved=Data.Read(path);var result=saved["result"]!.AsObject();
   if(saved.Text("key")==key&&DateTimeOffset.TryParse(saved.Text("updated"),out var time)&&time<=DateTimeOffset.UtcNow&&DateTimeOffset.UtcNow-time<TimeSpan.FromHours(24)&&valid(result)) {
    progress.Report("Reusing cached subtitle search results.");return result;
   }
  }catch(Exception ex) when(ex is IOException or InvalidDataException or JsonException or InvalidOperationException or ArgumentException or NullReferenceException){ }
  var fresh=await fetch();ct.ThrowIfCancellationRequested();
  if(valid(fresh))Data.Save(path,new{key,updated=DateTimeOffset.UtcNow,result=fresh});
  return fresh;
  }
 }
}
