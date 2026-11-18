using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;

// Short CPU kernel measurements only. Never applies settings or matches an episode.
static class ThreadBenchmark {
 public static async Task Run(string fixturePath,string directory) {
  var fixture=JsonNode.Parse(File.ReadAllText(fixturePath))!;var settings=fixture["settings"]!.Deserialize<AppSettings>(Data.Options)!;
  if(settings.Provider!="managed"||settings.GpuLayers!=0||settings.ThinkingEnabled!=false)throw new InvalidOperationException("CPU-only, thinking-off settings required.");
  var root=Path.GetFullPath(directory);if(Directory.Exists(root)&&Directory.EnumerateFileSystemEntries(root).Any())throw new InvalidOperationException("Use an empty tuning directory.");
  Directory.CreateDirectory(root);var logical=Path.Combine(root,"data");ScoutStorage.Register(logical,new ScoutDatabase(Path.Combine(root,"benchmark.db")));
  void Save(string name,object data)=>Data.Save(Path.Combine(logical,name),data);
  using var cancel=new CancellationTokenSource(TimeSpan.FromMinutes(45));using var idle=new ScoutIdleGate(fixture.Text("idle_status_database"),new Uri(settings.LocalEndpoint).GetLeftPart(UriPartial.Authority));
  await idle.Wait(Console.WriteLine,cancel.Token);
  var profile=ModelDownloads.Profiles.Single(p=>p.Id==settings.DownloadProfile);
  await using(var file=File.OpenRead(settings.ModelFile))if(Convert.ToHexStringLower(await SHA256.HashDataAsync(file,cancel.Token))!=profile.Sha256)throw new IOException("Model checksum mismatch.");
  // Validate the installed, pinned runtime without downloads.
  settings.AllowDownloads=false;settings.AutoDownloadMissing=false;
  using var http=new HttpClient();var exe=await RuntimeDownloads.EnsureAsync(settings,http,new Progress<string>(Console.WriteLine),cancel.Token);
  var start=new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(exe)!,"llama-bench.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
  foreach(var arg in new[]{"-m",settings.ModelFile,"-ngl","0","-t","8,12,16,24","-p","1024","-n","0","-r","2","-o","json","--offline"})start.ArgumentList.Add(arg);
  var watch=Stopwatch.StartNew();using var process=Process.Start(start)??throw new IOException("Could not start isolated thread benchmark.");
  var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
  Save("status.json",new{state="running",pid=process.Id,started=DateTimeOffset.UtcNow,arguments=start.ArgumentList.ToArray()});
  string failure="";
  try {
   while(!process.HasExited) {
    await Task.Delay(TimeSpan.FromSeconds(10),cancel.Token);
    if(!process.HasExited) {var state=await idle.Check(cancel.Token);if(!state.Idle)throw new InvalidOperationException("User Scout became active: "+state.Detail);}
   }
  }catch(Exception ex){failure=ex.Message;if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync();}}
  var text=await stdout;Save("raw.json",new{stdout=text,stderr=await stderr,exitCode=process.ExitCode,seconds=watch.Elapsed.TotalSeconds});
  if(failure!=""||process.ExitCode!=0){Save("status.json",new{state="failed",failure});throw new IOException(failure==""?"Thread benchmark failed; see raw diagnostics.":failure);}
  var rows=JsonNode.Parse(text)!.AsArray();var best=rows.OrderByDescending(r=>r!["avg_ts"]!.GetValue<double>()).First()!;
  Save("measurements.json",new{rows,bestThreads=best.Int("n_threads"),seconds=watch.Elapsed.TotalSeconds,limitations="Two 1024-token synthetic prompt trials per setting. Screening only, not a long-context guarantee. No generation or episode decisions, no settings applied."});
  Save("status.json",new{state="complete",bestThreads=best.Int("n_threads"),seconds=watch.Elapsed.TotalSeconds});Console.WriteLine(rows.ToJsonString(Data.Options));
 }
}
