using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Scout.Core;

if(args.Length==1&&args[0]=="--self-test"){TranscriptComparisonTests.Test();SubtitleTextTests.Test();ProviderMappingTests.Run();await SubdlApiTests.Run();return;}
if(args.Length==2&&args[0]=="--render"){SubtitleReport.Write(args[1]);return;}
if(args.Length==4&&args[0]=="--audit-key"){CredentialAudit.Run(args[1],args[2],args[3]);return;}
if(args.Length==6&&args[0]=="--map"){await MappedBenchmark.Run(args[1],args[2],args[3],args[4],args[5]);return;}
if(args.Length==6&&args[0]=="--prepare-map"){await MappedBenchmark.Run(args[1],args[2],args[3],args[4],args[5],true);return;}
if(args.Length==3&&args[0]=="--score-map"){MappedBenchmark.Score(args[1],args[2]);return;}
if(args.Length==4&&args[0]=="--offline-map"){MappedBenchmark.Offline(args[1],args[2],args[3]);return;}
if(args.Length==4&&args[0]=="--app-replay"){await AppReplay.Run(args[1],args[2],args[3]);return;}
if(args.Length!=3&&!(args.Length==5&&args[0]=="--api"))throw new ArgumentException("Scout.SubtitleBenchmark --api <saved dialogue fixture.json> <empty output folder> [<Scout database> <settings root>], or <fixture.json> <public-reference-pages.json> <empty output folder>");
bool apiMode=args[0]=="--api";var fixturePath=args[apiMode?1:0];
var fixture=JsonNode.Parse(File.ReadAllText(fixturePath))!;var pages=apiMode?new JsonArray():JsonNode.Parse(File.ReadAllText(args[1]))!.AsArray();
var root=Path.GetFullPath(args[2]);if(Directory.Exists(root)&&Directory.EnumerateFileSystemEntries(root).Any())throw new InvalidOperationException("Use an empty output directory.");
Directory.CreateDirectory(root);var logical=Path.Combine(root,"data");ScoutStorage.Register(logical,new ScoutDatabase(Path.Combine(root,"benchmark.db")));
void Save(string name,object data)=>Data.Save(Path.Combine(logical,name),data);
using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(45)};
http.DefaultRequestHeaders.UserAgent.ParseAdd("Scout-Subtitle-Feasibility/0.1");
var watch=Stopwatch.StartNew();var references=new List<Reference>();var downloads=new JsonArray();var failures=new JsonArray();
Save("manifest.json",new{fixtureSha256=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fixturePath))),pages,started=DateTimeOffset.UtcNow,
 mode=apiMode?"subdl-api-subtitle-text-feasibility":"public-subtitle-text-feasibility",apiSearch=apiMode,llmCalls=0,mediaAccess=false,appDatabaseWrites=false,manualCorrections=false,
 credentials=apiMode?"Windows-protected Scout credential read from a read-only database connection; HTTPS Authorization header only":"none"});
if(apiMode) {
 try {await ApiReferenceSearch.Load(fixture,references,downloads,failures,Save,CancellationToken.None,args.Length==5?args[3]:null,args.Length==5?args[4]:null);}
 catch(Exception ex) when(ex is SubdlAccessException or InvalidOperationException or InvalidDataException or System.Security.Cryptography.CryptographicException) {
  failures.Add(new JsonObject{["page"]="https://api.subdl.com",["error"]=ex.Message});Console.WriteLine("API test blocked: "+ex.Message);
 }
 Save("downloads.json",new{downloads,failures});
}
foreach(var page in pages) {
 string series=page.Text("series"),url=page.Text("url");
 try {
  if(new Uri(url).Host!="subdl.com")throw new InvalidDataException("Only explicit public SubDL pages are supported in this isolated prototype.");
  Console.WriteLine("Reading public reference page: "+url);
  var html=Encoding.UTF8.GetString(await Get(url,4*1024*1024));
  var anchors=Regex.Matches(html,"<a\\b[^>]*href=[\"'](?<url>https://dl\\.subdl\\.com/[^\"']+)[\"'][^>]*>(?<label>.*?)</a>",RegexOptions.IgnoreCase|RegexOptions.Singleline,TimeSpan.FromSeconds(2));
  var links=anchors.Where(m=>Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(m.Groups["label"].Value,"<[^>]+>"," ")),@"\s+"," ").Contains("Download English Subtitle",StringComparison.OrdinalIgnoreCase))
   .Select(m=>WebUtility.HtmlDecode(m.Groups["url"].Value)).Where(s=>new Uri(s).AbsolutePath.EndsWith(".zip",StringComparison.OrdinalIgnoreCase)).Distinct().ToArray();
  if(links.Length!=1)throw new InvalidDataException($"Expected one explicit public ZIP download; found {links.Length}. No hidden endpoint or access-control bypass attempted.");
  await Task.Delay(1000);var bytes=await Get(links[0],12*1024*1024);var sha=Convert.ToHexStringLower(SHA256.HashData(bytes));
  using var stream=new MemoryStream(bytes);using var zip=new ZipArchive(stream,ZipArchiveMode.Read);
  if(zip.Entries.Count>150||zip.Entries.Sum(e=>e.Length)>24*1024*1024)throw new InvalidDataException("Reference archive exceeds the bounded test limits.");
  var imported=new List<Reference>();
  foreach(var entry in zip.Entries.Where(e=>e.Name.EndsWith(".srt",StringComparison.OrdinalIgnoreCase))) {
   if(entry.Length>2*1024*1024)throw new InvalidDataException("Oversized subtitle entry.");
   await using var input=entry.Open();var textBytes=await Bounded(input,2*1024*1024,CancellationToken.None);
   var decoded=SubtitleText.Decode(textBytes);var cues=MediaPipeline.ParseSrt(decoded.Text);if(cues.Count<20)continue;
   var id=CollectionResolver.Hash(series+"|"+sha+"|"+entry.FullName);var reference=new Reference(id,series,entry.FullName,url,TranscriptComparison.Index(cues.Select(c=>c.Text)));
   imported.Add(reference);Save("references/"+id+".json",new{reference.Id,reference.Series,reference.Name,reference.Url,zipSha256=sha,cues,encoding=decoded.Encoding});
  }
  if(imported.Count==0)throw new InvalidDataException("The archive contained no usable SRT dialogue.");
  references.AddRange(imported);downloads.Add(new JsonObject{["series"]=series,["page"]=url,["download"]=links[0],["sha256"]=sha,["subtitle_files"]=imported.Count});
 }catch(Exception ex){failures.Add(new JsonObject{["page"]=url,["error"]=ex.Message});Console.WriteLine("Reference unavailable: "+ex.Message);}
 Save("downloads.json",new{downloads,failures});
}
var matching=Stopwatch.StartNew();var rows=new JsonArray();
foreach(var input in fixture.Array("cases")) {
 var cues=input!["cues"]!.Deserialize<List<Cue>>(Data.Options)!;var index=TranscriptComparison.Index(cues.Select(c=>c.Text));
 var pool=references.Where(r=>r.Series==input.Text("series")).ToArray();var common=TranscriptComparison.Common(pool.Select(r=>r.Index).ToArray());
 var ranked=pool.Select(r=>new{reference=r,overlap=TranscriptComparison.Compare(index,r.Index,common)}).OrderByDescending(r=>r.overlap.Score).ThenBy(r=>r.reference.Id).ToArray();
 var best=ranked.FirstOrDefault();double margin=best==null?0:best.overlap.Score-(ranked.Skip(1).FirstOrDefault()?.overlap.Score??0);
 // Mapping happens after textual ranking and cannot influence the comparison.
 // Uploaded labels remain unverified; these are catalog candidates, not assignments.
 var catalog=input["catalog"]!.Deserialize<List<Episode>>(Data.Options)!;
 string Label(string value)=>" "+Regex.Replace(Regex.Replace(value.ToLowerInvariant().Replace("'","").Replace("’",""),@"\s*\(\d+\)\s*$",""),@"[^\p{L}\p{N}]+"," ").Trim()+" ";
 Episode[] mapped=best==null?[]:catalog.Where(e=>Label(best.reference.Name).Contains(Label(e.Title),StringComparison.Ordinal)).ToArray();
 // Predeclared feasibility screen; never an app assignment or calibrated confidence.
 bool substantial=best!=null&&best.overlap.SharedPhrases>=60&&best.overlap.SourceCoverage>=.45&&best.overlap.ReferenceCoverage>=.45&&best.overlap.SourceSections>=3&&best.overlap.ReferenceSections>=3&&margin>=.15;
 var row=new JsonObject{["case_id"]=input["case_id"]!.DeepClone(),["source"]=input["source"]!.DeepClone(),["series"]=input["series"]!.DeepClone(),
  ["reference_files_searched"]=pool.Length,["substantial_text_overlap"]=substantial,["margin"]=margin,
  ["catalog_candidates_from_reference_title"]=JsonSerializer.SerializeToNode(mapped.Select(e=>new{e.Id,e.Code,e.Title}),Data.Options),
  ["catalog_provider"]=fixture["settings"].Text("metadata_provider"),["catalog_mapping_verified"]=false,
  ["ranking"]=JsonSerializer.SerializeToNode(ranked.Take(5).Select(r=>new{r.reference.Id,r.reference.Name,r.reference.Url,r.overlap}),Data.Options)};
 rows.Add(row);Save("cases/"+input.Text("case_id")+".json",row);
}
matching.Stop();var summary=new{cases=rows.Count,referenceFiles=references.Count,substantialOverlap=rows.Count(r=>r.Bool("substantial_text_overlap")),remaining=rows.Count(r=>!r.Bool("substantial_text_overlap")),
 downloadFailures=failures.Count,matchingSeconds=matching.Elapsed.TotalSeconds,totalSeconds=watch.Elapsed.TotalSeconds,llmCalls=0,appDatabaseWrites=false,mediaAccess=false,manualCorrections=false,
 limitations=(apiMode?"Authenticated API feasibility test, not a production assignment policy. One English season pack per catalog season is selected automatically. ":"Public download feasibility only; authenticated search requires a saved API key. ")+"Uploaded reference labels and title-derived catalog candidates are unverified. Scores measure shared five-word sequences, not calibrated confidence. No provider-ID assignments or complete/multipart claims are made. Unavailable episodes and edition/transcription differences may prevent matching."};
Save("results.json",new{cases=rows});Save("summary.json",summary);Save("status.json",new{state=references.Count==0?"blocked":"complete",updated=DateTimeOffset.UtcNow});
SubtitleReport.Write(root);Console.WriteLine(JsonSerializer.Serialize(summary,Data.Options));
if(references.Count==0)Environment.ExitCode=2;

async Task<byte[]> Get(string url,int limit) {
 var uri=new Uri(url);if(uri.Scheme!="https"||uri.Host is not ("subdl.com" or "dl.subdl.com")||uri.UserInfo!="")throw new InvalidDataException("Unsupported reference URL.");
 using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));
 using var response=await http.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,timeout.Token);response.EnsureSuccessStatusCode();
 if(response.Content.Headers.ContentLength>limit)throw new InvalidDataException("Reference response exceeds the size limit.");
 await using var input=await response.Content.ReadAsStreamAsync(timeout.Token);return await Bounded(input,limit,timeout.Token);
}
static async Task<byte[]> Bounded(Stream input,int limit,CancellationToken ct) {
 using var output=new MemoryStream();var buffer=new byte[65536];int count;
 while((count=await input.ReadAsync(buffer,ct))>0){if(output.Length+count>limit)throw new InvalidDataException("Reference response exceeds the size limit.");output.Write(buffer,0,count);}
 return output.ToArray();
}
