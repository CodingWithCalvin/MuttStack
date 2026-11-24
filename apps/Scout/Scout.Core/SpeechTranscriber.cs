using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace Scout.Core;

public sealed record SpeechRequest(string Path,double Duration,int AudioTrack,string Ffmpeg,string CacheDirectory,string SourceKey);
public sealed record SpeechTranscript(IReadOnlyList<Cue> Cues,JsonObject Provenance);
public sealed record SpeechSpan(double Start,double End,string Text);
public interface ISpeechTranscriber {
    string ConfigurationKey {get;}
    Task<SpeechTranscript> TranscribeAsync(SpeechRequest request,IProgress<string> progress,CancellationToken ct,ScoutPause? pause=null);
}

public sealed class SpeechTranscriber : ISpeechTranscriber {
    public const string Revision="local-speech-v1";
    readonly SpeechSettings settings;
    readonly string directory;
    readonly Func<string,IEnumerable<string>,CancellationToken,Task<string>> execute;
    readonly Func<IProgress<string>,CancellationToken,Task<SpeechAssets>> ensure;
    SpeechAssets? assets;
    public string ConfigurationKey {get;}
    public SpeechTranscriber(SpeechSettings settings,string modelDirectory,
        Func<string,IEnumerable<string>,CancellationToken,Task<string>>? execute=null,
        Func<IProgress<string>,CancellationToken,Task<SpeechAssets>>? ensure=null) {
        settings.Validate();this.settings=settings;directory=modelDirectory;
        this.execute=execute??ExecuteAsync;this.ensure=ensure??EnsureAsync;
        ConfigurationKey=CollectionResolver.Hash(JsonSerializer.Serialize(new{Revision,settings.Model,settings.Device,settings.Threads,settings.ChunkSeconds,Language="en",ModelHash=SpeechModels.Profile(settings).Model.Sha256,Runtime=SpeechModels.Runtime(settings).Sha256,Vad=SpeechModels.Profile(settings).Engine=="whisper"?SpeechModels.Vad.Sha256:""}));
    }
    async Task<SpeechAssets> EnsureAsync(IProgress<string> progress,CancellationToken ct) {
        using var http=new HttpClient(); // Run-time preparation verifies local files; only the settings download action uses the network.
        return await SpeechModels.EnsureAsync(settings,directory,false,http,progress,ct);
    }
    async Task<string> ExecuteAsync(string exe,IEnumerable<string> args,CancellationToken ct) {
        var start=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in args)start.ArgumentList.Add(arg);
        start.Environment["NEMO_SPEECH_MODEL_DIR"]=SpeechModels.DirectoryFor(directory);
        start.Environment["OMP_NUM_THREADS"]=settings.Threads.ToString(CultureInfo.InvariantCulture);start.Environment["NO_COLOR"]="1";
        using var process=Process.Start(start)??throw new IOException("Cannot start "+Path.GetFileName(exe));
        using var cancel=ct.Register(()=>{try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}});
        var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
        try {
            await process.WaitForExitAsync(ct);var stderr=await error;var stdout=await output;ct.ThrowIfCancellationRequested();
            if(process.ExitCode!=0)throw new IOException(Path.GetFileName(exe)+" failed: "+stderr[..Math.Min(stderr.Length,900)]);
            return stdout;
        } finally {
            if(!process.HasExited){try{process.Kill(true);}catch(InvalidOperationException){}await process.WaitForExitAsync(CancellationToken.None);}
            await Task.WhenAll(output,error);
        }
    }
    public async Task<SpeechTranscript> TranscribeAsync(SpeechRequest request,IProgress<string> progress,CancellationToken ct,ScoutPause? pause=null) {
        if(!double.IsFinite(request.Duration)||request.Duration<=0||request.Duration>86400||request.AudioTrack<0)
            throw new InvalidDataException("Speech transcription requires a valid audio track and duration (up to 24 hours).");
        if(PreviewPlanner.Within(request.CacheDirectory,Path.GetDirectoryName(Path.GetFullPath(request.Path))!))
            throw new InvalidOperationException("Speech cache must be outside the media folder.");
        var profile=SpeechModels.Profile(settings);var spans=new List<SpeechSpan>();int chunks=(int)Math.Ceiling(request.Duration/settings.ChunkSeconds);
        var cacheRoot=Path.Combine(request.CacheDirectory,CollectionResolver.Hash(request.SourceKey+"|"+request.AudioTrack+"|"+ConfigurationKey));
        var temporary=Path.Combine(Path.GetTempPath(),"Scout-speech-"+Guid.NewGuid().ToString("N"));
        try {
            for(int i=0;i<chunks;i++) {
                pause?.Checkpoint();ct.ThrowIfCancellationRequested();
                double start=i*settings.ChunkSeconds,end=Math.Min(request.Duration,start+settings.ChunkSeconds);
                double extractStart=Math.Max(0,start-2),extractEnd=Math.Min(request.Duration,end+2),length=extractEnd-extractStart;
                var cachePath=Path.Combine(cacheRoot,i.ToString("D5")+".json");
                List<SpeechSpan>? chunk=ReadCache(cachePath,length);
                if(chunk!=null)progress.Report($"Reusing speech transcript [{i+1}/{chunks}] {Path.GetFileName(request.Path)} · {profile.Name}");
                else {
                    assets??=await ensure(progress,ct);
                    Directory.CreateDirectory(temporary);var wav=Path.Combine(temporary,"audio.wav");var json=Path.Combine(temporary,"transcript.json");
                    var label=$"Transcribing [{i+1}/{chunks}] {Path.GetFileName(request.Path)} · {profile.Name} · {TimeSpan.FromSeconds(start):hh\\:mm\\:ss}–{TimeSpan.FromSeconds(end):hh\\:mm\\:ss}";
                    progress.Report(label);
                    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
                    using var heartbeatStop=CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    var heartbeat=Heartbeat(label,progress,heartbeatStop.Token);
                    try {
                        if(File.Exists(json))File.Delete(json);
                        await execute(request.Ffmpeg,["-nostdin","-v","error","-y","-ss",Number(extractStart),"-i",request.Path,"-t",Number(length),"-map","0:"+request.AudioTrack,"-vn","-sn","-dn","-ac","1","-ar","16000","-c:a","pcm_s16le",wav],timeout.Token);
                        if(!File.Exists(wav)||new FileInfo(wav).Length<44)throw new IOException("Audio extraction produced no usable WAV data.");
                        var args=Arguments(profile,assets,wav,json,settings);
                        await execute(assets.Executable,args,timeout.Token);
                        if(!File.Exists(json)||new FileInfo(json).Length>16*1024*1024)throw new InvalidDataException("Speech engine produced no usable transcript JSON.");
                        chunk=ParseOutput(profile.Engine,JsonNode.Parse(await File.ReadAllTextAsync(json,timeout.Token))!,length);
                        timeout.Token.ThrowIfCancellationRequested();
                        var data=JsonSerializer.SerializeToNode(chunk,Data.Options)!;
                        Data.Save(cachePath,new JsonObject{["revision"]=Revision,["duration"]=length,["spans"]=data,["hash"]=CollectionResolver.Hash(data.ToJsonString())});
                    } catch(OperationCanceledException) when(!ct.IsCancellationRequested) {
                        throw new IOException($"Speech transcription timed out on chunk {i+1}/{chunks}. Completed chunks are saved; resume to retry.");
                    } finally {heartbeatStop.Cancel();await heartbeat;}
                }
                // Each overlap belongs to only one central interval. Repeated dialogue elsewhere is retained.
                spans.AddRange(chunk.Where(s=>s.Start+(s.End-s.Start)/2+extractStart>=start&&(i==chunks-1||s.Start+(s.End-s.Start)/2+extractStart<end))
                    .Select(s=>new SpeechSpan(s.Start+extractStart,s.End+extractStart,s.Text)));
            }
            ct.ThrowIfCancellationRequested();var cues=ToCues(spans);
            progress.Report($"Transcribed {Path.GetFileName(request.Path)} · {profile.Name} · {cues.Count} dialogue passages (machine transcription).");
            return new(cues,new JsonObject{["method"]="local_speech",["model"]=settings.Model,["name"]=profile.Name,["configuration"]=ConfigurationKey,["revision"]=Revision,["model_sha256"]=profile.Model.Sha256,["runtime_sha256"]=SpeechModels.Runtime(settings).Sha256,["device"]=settings.Device,["language"]="en",["audio_track"]=request.AudioTrack,["chunk_seconds"]=settings.ChunkSeconds,["complete"]=true});
        } finally {if(Directory.Exists(temporary))Directory.Delete(temporary,true);}
    }
    static string Number(double value)=>value.ToString("0.###",CultureInfo.InvariantCulture);
    public static IReadOnlyList<string> Arguments(SpeechProfile profile,SpeechAssets assets,string wav,string output,SpeechSettings settings) {
        if(profile.Engine=="parakeet")return ["transcribe",wav,"--model",assets.Model,"--device",settings.Device=="cuda"?"cuda:0":"cpu","--language","en","--format","json","--output",output];
        var args=new List<string>{"-m",assets.Model,"-f",wav,"-l","en","-t",settings.Threads.ToString(CultureInfo.InvariantCulture),"-oj","-of",Path.ChangeExtension(output,null),"-np","--vad","--vad-model",assets.Vad??throw new InvalidOperationException("Whisper requires its speech detector.")};
        if(settings.Device=="cpu")args.Add("-ng");return args;
    }
    static async Task Heartbeat(string label,IProgress<string> progress,CancellationToken ct) {
        var clock=Stopwatch.StartNew();using var timer=new PeriodicTimer(TimeSpan.FromSeconds(10));
        try{while(await timer.WaitForNextTickAsync(ct))progress.Report(label+$" · {clock.Elapsed.TotalSeconds:0}s elapsed");}catch(OperationCanceledException) when(ct.IsCancellationRequested){}
    }
    static List<SpeechSpan>? ReadCache(string path,double duration) {
        if(!ScoutStorage.Exists(path))return null;
        try {
            var data=Data.Read(path);if(data.Text("revision")!=Revision||data["duration"]?.GetValue<double>()!=duration||data["spans"] is not JsonArray rows||CollectionResolver.Hash(rows.ToJsonString())!=data.Text("hash"))return null;
            var spans=rows.Deserialize<List<SpeechSpan>>(Data.Options)!;ValidateSpans(spans,duration);return spans;
        }catch(Exception ex) when(ex is JsonException or InvalidDataException or InvalidOperationException or FormatException){return null;}
    }
    public static List<SpeechSpan> ParseOutput(string engine,JsonNode output,double duration) {
        try {return ParseSpans(engine,output,duration);}
        catch(Exception ex) when(ex is JsonException or InvalidOperationException or FormatException or NullReferenceException) {throw new InvalidDataException("Speech engine returned malformed timestamp data.",ex);}
    }
    static List<SpeechSpan> ParseSpans(string engine,JsonNode output,double duration) {
        var spans=new List<SpeechSpan>();
        if(engine=="parakeet") {
            if(output["words"] is not JsonArray words)throw new InvalidDataException("Parakeet returned no word timing data.");
            foreach(var word in words)spans.Add(new(word!["start"]!.GetValue<double>(),word["end"]!.GetValue<double>(),word.Text("word").Trim()));
            if(spans.Count==0&&!string.IsNullOrWhiteSpace(output.Text("text")))throw new InvalidDataException("Parakeet transcript has text without timestamps.");
        } else if(engine=="whisper") {
            if(output["transcription"] is not JsonArray segments)throw new InvalidDataException("Whisper returned no transcription data.");
            foreach(var segment in segments)spans.Add(new(segment!["offsets"]!["from"]!.GetValue<double>()/1000,segment["offsets"]!["to"]!.GetValue<double>()/1000,segment.Text("text").Trim()));
        } else throw new InvalidDataException("Unknown speech output format.");
        ValidateSpans(spans,duration);return spans.Where(s=>!string.IsNullOrWhiteSpace(s.Text)).Select(s=>s with{Start=Math.Min(s.Start,duration),End=Math.Min(s.End,duration)}).ToList();
    }
    static void ValidateSpans(IReadOnlyList<SpeechSpan> spans,double duration) {
        if(spans.Any(s=>s==null||!double.IsFinite(s.Start)||!double.IsFinite(s.End)||s.Start<0||s.End<s.Start||s.End>duration+.5||s.Text==null)||spans.Zip(spans.Skip(1)).Any(p=>p.First.Start>p.Second.Start))
            throw new InvalidDataException("Speech timestamps are invalid or outside the extracted audio.");
    }
    static List<Cue> ToCues(IEnumerable<SpeechSpan> spans) {
        var cues=new List<Cue>();var text=new List<string>();double start=0,end=0;
        foreach(var span in spans.OrderBy(s=>s.Start)) {
            if(text.Count>0&&(span.Start-start>=8||span.Start-end>1.5||text.Count>=24)) {cues.Add(new(start,string.Join(" ",text)));text.Clear();}
            if(text.Count==0)start=span.Start;text.Add(span.Text);end=span.End;
        }
        if(text.Count>0)cues.Add(new(start,string.Join(" ",text)));return cues;
    }
}
