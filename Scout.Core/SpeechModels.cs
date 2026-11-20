using System.IO.Compression;
using System.Security.Cryptography;
namespace Scout.Core;

public sealed record SpeechProfile(string Id,string Name,ModelProfile Model,string Engine) {
    public string Display=>$"{Name} · {Model.Bytes/1e9:0.00} GB";
}
public sealed record SpeechAssets(string Executable,string Model,string? Vad,string Runtime);
public sealed class SpeechNotReadyException(string message):IOException(message);

// Explicitly selected, pinned native engines. No Python installation or model hub login is needed.
public static class SpeechModels {
    public static IReadOnlyList<SpeechProfile> Profiles {get;}=[
        new("parakeet-v3","Parakeet TDT 0.6B v3",Asset("parakeet-tdt-0.6b-v3.q8_0.gguf",713975456,"e3880d0aaaaf2c308ea2c35016b2b895c423eb3fda924c1b463d1c19b7f4d32e","https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3/resolve/541d1f99c6b0c3cd0b11a95167540bb8edefd82b/"),"parakeet"),
        new("whisper-turbo","Whisper Turbo + speech detection",Asset("ggml-large-v3-turbo-q8_0.bin",874188075,"317eb69c11673c9de1e1f0d459b253999804ec71ac4c23c17ecf5fbe24e259a1","https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/"),"whisper")
    ];
    public static ModelProfile Vad {get;}=Asset("ggml-silero-v6.2.0.bin",885098,"2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987","https://huggingface.co/ggml-org/whisper-vad/resolve/9ffd54a1e1ee413ddf265af9913beaf518d1639b/");
    static ModelProfile Asset(string name,long bytes,string sha,string prefix)=>new(name,name,name,bytes,sha,prefix+name,true,"Pinned speech asset");
    public static SpeechProfile Profile(SpeechSettings s)=>Profiles.First(p=>p.Id==s.Model);
    public static ModelProfile Runtime(SpeechSettings s) {
        if(Profile(s).Engine=="parakeet")return s.Device=="cuda"
            ?Asset("nemo-speech-0.3.0-windows-x86_64-cuda.zip",508625181,"d0d32ae1bf6ee0534767a47b2aa0e9e2ff6fe39643465c0baae9301d04f6ed42","https://github.com/NVIDIA/NeMo-Speech.cpp/releases/download/v0.3.0/")
            :Asset("nemo-speech-0.3.0-windows-x86_64-cpu.zip",8694509,"4ed9740e361e4653f56c3dce7d1e81883a1c8b831c1bd7d01567fff1130b5b05","https://github.com/NVIDIA/NeMo-Speech.cpp/releases/download/v0.3.0/");
        return s.Device=="cuda"
            ?Asset("whisper-bin-win-cuda-12.4.0-x64.zip",684913404,"afef0b881c500958921c3f5523b50e59ee2ec9b6f5cbd25b324c51ed308a957a","https://github.com/ggml-org/whisper.cpp/releases/download/b5454/")
            :Asset("whisper-bin-x64.zip",8928640,"6ba69e3482d7826214f90a6a9c84ca07782aec1e1d0c6a7c30c994fd5d816ccb","https://github.com/ggml-org/whisper.cpp/releases/download/b5454/");
    }
    public static string DirectoryFor(string modelDirectory)=>Path.Combine(modelDirectory,"speech");
    public static string Installation(SpeechSettings s,string directory) {
        s.Validate();var root=DirectoryFor(directory);var files=new[]{Profile(s).Model,Runtime(s)}.Concat(Profile(s).Engine=="whisper"?[Vad]:[]);
        return files.All(p=>File.Exists(Path.Combine(root,p.FileName)))?"Downloaded · verified before use":"Not downloaded · use Download / verify speech model";
    }
    public static async Task<SpeechAssets> EnsureAsync(SpeechSettings s,string directory,bool allowDownload,HttpClient http,IProgress<string> progress,CancellationToken ct) {
        s.Validate();
        if(!OperatingSystem.IsWindows()||System.Runtime.InteropServices.RuntimeInformation.OSArchitecture!=System.Runtime.InteropServices.Architecture.X64)
            throw new PlatformNotSupportedException("Managed speech transcription requires Windows x64.");
        var root=DirectoryFor(directory);var profile=Profile(s);var runtime=Runtime(s);
        var required=new[]{profile.Model,runtime}.Concat(profile.Engine=="whisper"?[Vad]:[]).ToList();
        if(!allowDownload&&required.Any(p=>!File.Exists(Path.Combine(root,p.FileName))))
            throw new SpeechNotReadyException("Download the selected speech model in Settings > Speech, then reanalyze this Scout.");
        foreach(var asset in required) {
            var path=Path.Combine(root,asset.FileName);progress.Report("Verifying "+asset.Name+"…");
            if(File.Exists(path)) {
                await using var file=File.OpenRead(path);
                if(file.Length!=asset.Bytes||Convert.ToHexStringLower(await SHA256.HashDataAsync(file,ct))!=asset.Sha256)
                    throw new IOException("Speech asset verification failed: "+asset.FileName+". Choose another model storage folder or restore the verified file.");
            } else {
                if(!allowDownload)throw new SpeechNotReadyException("Speech assets are missing. Download / verify the selected model in Settings > Speech.");
                await ModelDownloads.DownloadAsync(asset,new AppSettings{ModelDirectory=root,AllowDownloads=true},http,progress,ct);
            }
        }
        var target=Path.Combine(root,"runtimes",runtime.Sha256[..16]);
        var exe=await InstallAsync(Path.Combine(root,runtime.FileName),target,profile.Engine=="parakeet"?"nemo-speech.exe":"whisper-cli.exe",ct);
        progress.Report(profile.Name+" ready · "+(s.Device=="cuda"?"NVIDIA GPU":"CPU"));
        return new(exe,Path.Combine(root,profile.Model.FileName),profile.Engine=="whisper"?Path.Combine(root,Vad.FileName):null,runtime.Sha256);
    }
    public static async Task<string> InstallAsync(string archive,string target,string executable,CancellationToken ct) {
        using var zip=ZipFile.OpenRead(archive);var entries=zip.Entries.Where(e=>e.Name!="").ToList();
        if(entries.Count==0||entries.Count>2000||entries.Sum(e=>e.Length)>3L*1024*1024*1024)throw new InvalidDataException("Unexpected speech runtime archive size.");
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var e in zip.Entries) {
            var parts=e.FullName.Replace('\\','/').Split('/');
            if(Path.IsPathRooted(e.FullName)||parts.Any(p=>p is "." or ".."||p.Contains(':'))||((e.ExternalAttributes>>16)&0xF000)==0xA000||!names.Add(e.FullName.Replace('\\','/')))
                throw new InvalidDataException("Unsafe speech runtime archive path.");
        }
        var executables=entries.Where(e=>e.Name==executable).ToList();
        if(executables.Count!=1)throw new InvalidDataException("Speech archive must contain exactly one "+executable+".");
        var installed=Directory.Exists(target);var staging=installed?target:target+"."+Guid.NewGuid().ToString("N")+".partial";
        try {
            foreach(var entry in entries) {
                ct.ThrowIfCancellationRequested();var path=Path.Combine(staging,entry.FullName);await using var input=entry.Open();
                if(installed) {
                    if(!File.Exists(path))throw new IOException("Speech runtime is incomplete. Restore its version folder before retrying.");
                    await using var actual=File.OpenRead(path);
                    var expectedHash=await SHA256.HashDataAsync(input,ct);var actualHash=await SHA256.HashDataAsync(actual,ct);
                    if(actual.Length!=entry.Length||!CryptographicOperations.FixedTimeEquals(expectedHash,actualHash))
                        throw new IOException("Speech runtime verification failed. Restore its version folder before retrying.");
                } else {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);await using var output=new FileStream(path,FileMode.CreateNew);await input.CopyToAsync(output,ct);
                }
            }
            if(!installed){ct.ThrowIfCancellationRequested();Directory.CreateDirectory(Path.GetDirectoryName(target)!);Directory.Move(staging,target);}
            return Path.Combine(target,executables[0].FullName);
        } finally {if(!installed&&Directory.Exists(staging))Directory.Delete(staging,true);}
    }
}
