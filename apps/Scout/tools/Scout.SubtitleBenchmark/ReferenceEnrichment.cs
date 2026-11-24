using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;


static class ReferenceEnrichment {
 public static MappedReference Read(JsonNode node,IReadOnlyList<Episode> catalog) {
  var cues=node["cues"]!.Deserialize<List<Cue>>(Data.Options)!;
  var reference=new Reference(node.Text("id"),node.Text("series"),node.Text("name"),node.Text("url"),TranscriptComparison.Index(cues.Select(c=>c.Text)));
  return new(reference,node.Text("zip_sha256"),node.Int("season"),TitleMapping.Label(reference.Name),TitleMapping.Find(reference.Name,catalog,reference.Id));
 }
 public static async Task AddAnchors(SubdlApi api,JsonNode identity,ProviderSnapshot guide,List<MappedReference> references,Action<string,object> save,CancellationToken ct,Dictionary<string,JsonNode>? cachedSearches=null) {
  string series=identity.Text("series");
  // Bound discovery to two additional title-bearing packs per relevant season.
  // Catalog titles choose metadata anchors; source filenames and expected labels
  // never participate. Existing downloads and dialogue are reused.
  var seasons=references.Where(r=>r.Reference.Series==series&&r.Link==null).Select(r=>r.Season).Distinct().Order().ToArray();
  foreach(int season in seasons) {
   var searchKey=$"anchor-search/{CollectionResolver.Hash(series)}/{season}.json";
   var response=cachedSearches?.GetValueOrDefault(searchKey)??await api.Subtitles(identity.Text("sd_id"),season,ct);
   save(searchKey,response);
   var existing=references.Select(r=>r.Reference.Url).ToHashSet();
   var packs=response.Array("subtitles").OfType<JsonObject>().Where(row=>row.Text("lang",row.Text("language")).ToLowerInvariant() is "" or "en" or "eng" or "english").Select(row=> {
    var files=row.Array("unpack_files").Select(n=>n is JsonValue?n.ToString():n.Text("name",n.Text("filename",n.Text("file")))).ToArray();
    Uri? uri=null;try{uri=SubdlApi.DownloadUri(row);}catch(InvalidDataException){}
    int titles=files.Count(name=>TitleMapping.Find(name,guide.Episodes,"")!=null);
    return new{row,files,titles,uri};
   }).Where(p=>p.uri!=null&&p.titles>0)
    .OrderByDescending(p=>p.titles).ThenBy(p=>p.uri!.AbsolutePath,StringComparer.Ordinal).Take(2)
    .Where(p=>!existing.Contains(new UriBuilder(p.uri!){Query=""}.Uri.AbsoluteUri)).ToArray();
   save($"anchor-selection/{CollectionResolver.Hash(series)}/{season}.json",new{series,season,selected=packs.Select(p=>new{url=new UriBuilder(p.uri!){Query=""}.Uri.AbsoluteUri,titleAnchors=p.titles}),policy="At most two new packs with most uniquely mapped provider titles; deterministic URL tie break; no source labels or expected results."});
   foreach(var pack in packs) {
    var bytes=await api.Download(pack.uri!,ct);var sha=Convert.ToHexStringLower(SHA256.HashData(bytes));
    var url=new UriBuilder(pack.uri!){Query=""}.Uri.AbsoluteUri;save("archives/"+sha+".json",new{sha256=sha,bytes,series,season,url});
    using var zip=new ZipArchive(new MemoryStream(bytes),ZipArchiveMode.Read);
    if(zip.Entries.Count>150||zip.Entries.Sum(e=>e.Length)>24*1024*1024)throw new InvalidDataException("Oversized reference pack.");
    foreach(var entry in zip.Entries.Where(e=>e.Name.EndsWith(".srt",StringComparison.OrdinalIgnoreCase))) {
     using var stream=entry.Open();var raw=await SubdlApi.Bounded(stream,2*1024*1024,ct);var decoded=SubtitleText.Decode(raw);
     var cues=MediaPipeline.ParseSrt(decoded.Text);if(cues.Count<20)continue;
     var id=CollectionResolver.Hash(series+"|"+sha+"|"+entry.FullName);
     var node=JsonSerializer.SerializeToNode(new{id,series,name=entry.FullName,url,zipSha256=sha,cues,season,encoding=decoded.Encoding},Data.Options)!;
     if(references.All(r=>r.Reference.Id!=id)){references.Add(Read(node,guide.Episodes));save("references/"+id+".json",node);}
    }
   }
  }
 }
}
