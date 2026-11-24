using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
namespace Scout.Core;
public sealed class LocalRuntime : IAsyncDisposable
{
 private readonly Process process;
 private LocalRuntime(Process p){process=p;}
 public static async Task<LocalRuntime> StartAsync(AppSettings s,IProgress<string> progress,CancellationToken ct) {
  using var downloadHttp=new HttpClient{Timeout=TimeSpan.FromHours(12)};
  if(!File.Exists(s.ModelFile)&&s.AutoDownloadMissing)
   s.ModelFile=await ModelDownloads.DownloadAsync(ModelDownloads.Profiles.Single(p=>p.Id==s.DownloadProfile),s,downloadHttp,progress,ct);
  if(!File.Exists(s.ModelFile))throw new FileNotFoundException("Choose a GGUF model or enable automatic model downloads in Settings.");
  var executable=await RuntimeDownloads.EnsureAsync(s,downloadHttp,progress,ct);
  var uri=DialogueMatcher.LocalEndpoint(s.LocalEndpoint);
  using(var socket=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp)) {
   try{socket.Bind(new IPEndPoint(IPAddress.Loopback,uri.Port));}catch(SocketException){throw new IOException("Local port occupied. Use the existing-endpoint mode or choose another port.");}
  }
  var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.GetDirectoryName(Path.GetFullPath(executable))!};
  foreach(var arg in Arguments(s,uri.Port))start.ArgumentList.Add(arg);
  var p=Process.Start(start)??throw new IOException("Cannot start local runtime.");var runtime=new LocalRuntime(p);
  p.OutputDataReceived+=(_,_)=>{};p.ErrorDataReceived+=(_,_)=>{};p.BeginOutputReadLine();p.BeginErrorReadLine();
  try {
   using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(2)};
   await WaitUntilReadyAsync(http,new Uri($"http://127.0.0.1:{uri.Port}/health"),()=>p.HasExited,progress,ct);
   return runtime;
  }catch{await runtime.DisposeAsync();throw;}
 }
 internal static async Task WaitUntilReadyAsync(HttpClient http,Uri health,Func<bool> exited,IProgress<string> progress,CancellationToken ct,TimeSpan? delay=null) {
   progress.Report("Loading local model…");
   for(int n=0;n<300;n++) {
    ct.ThrowIfCancellationRequested();if(exited())throw new IOException("Local runtime exited. Check compatibility, RAM, GPU layers and context size.");
    try{using var response=await http.GetAsync(health,ct);if(response.IsSuccessStatusCode){progress.Report("Local model ready. Starting analysis…");return;}}
    catch(HttpRequestException){}catch(TaskCanceledException) when(!ct.IsCancellationRequested){}
    await Task.Delay(delay??TimeSpan.FromSeconds(2),ct);
   }
   throw new TimeoutException("Local model did not become ready within 10 minutes.");
 }
 public static IReadOnlyList<string> Arguments(AppSettings s,int port) {
  s.Validate();
  var args=new List<string>{"-m",s.ModelFile,"--host","127.0.0.1","--port",port.ToString(),"-c",s.EffectiveContextSize.ToString(),"-ngl",s.GpuLayers.ToString(),"-np","1","--cache-prompt","-t",s.CpuThreads.ToString(),"-tb",s.EffectivePromptThreads.ToString(),"--alias",s.LocalModel,"--reasoning",s.ManagedThinking?"on":"off","--reasoning-budget",s.ManagedThinking?"2048":"0","--reasoning-format","deepseek"};
  if(s.ManagedFullSwaCache)args.Add("--swa-full");
  return args;
 }
 public async ValueTask DisposeAsync() {
  try{if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync();}}finally{process.Dispose();}
 }
}
