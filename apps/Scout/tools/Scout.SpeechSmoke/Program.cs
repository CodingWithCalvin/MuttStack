using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;
using Scout.Core.Subtitles;

// Explicit isolated smoke test: uses only previously extracted benchmark WAVs.
// Never opens the media paths recorded in clips.json or any live Scout database.
if(args.Length!=3)throw new ArgumentException("Usage: Scout.SpeechSmoke <benchmark folder> <isolated output folder> <ffmpeg>");
var benchmark=Path.GetFullPath(args[0]);var root=Path.GetFullPath(args[1]);var ffmpeg=args[2];Directory.CreateDirectory(root);
var modelDirectory=Path.Combine(root,"models");var clips=JsonNode.Parse(File.ReadAllText(Path.Combine(benchmark,"clips.json")))!.AsArray().Where(c=>c.Text("kind")=="speech").ToList();
var progress=new InlineProgress();var results=new JsonArray();var fixtureRoot=Path.Combine(root,"media");Directory.CreateDirectory(fixtureRoot);
var fixture=Path.Combine(fixtureRoot,"a.mkv");var cues=new List<Cue>();double duration=0;var inputs=new List<string>();
foreach(var clip in clips) {
    var id=clip.Text("id");if(id!=Path.GetFileName(id))throw new InvalidDataException("Invalid clip ID");inputs.AddRange(["-i",Path.Combine(benchmark,"clips",id+".wav")]);
    double sourceStart=clip!["start"]!.GetValue<double>();
    cues.AddRange(clip.Array("reference").Select(c=>new Cue(Math.Max(0,c!["start"]!.GetValue<double>()-sourceStart)+duration,c.Text("text"))));duration+=clip["duration"]!.GetValue<double>();
}
if(!File.Exists(fixture))await MediaPipeline.ExecuteAsync(ffmpeg,["-nostdin","-v","error","-f","lavfi","-i","color=c=black:s=16x16:r=1",..inputs,"-filter_complex",string.Concat(Enumerable.Range(1,clips.Count).Select(i=>$"[{i}:a]"))+$"concat=n={clips.Count}:v=0:a=1[a]","-map","0:v","-map","[a]","-t",duration.ToString(CultureInfo.InvariantCulture),"-c:v","libx264","-preset","ultrafast","-c:a","pcm_s16le",fixture],default);
var before=PreviewPlanner.SampledDigest(fixture);var originalMtime=File.GetLastWriteTimeUtc(fixture);
using var downloads=new HttpClient{Timeout=TimeSpan.FromHours(2)};
foreach(var model in new[]{"parakeet-v3","whisper-turbo"})foreach(var device in new[]{"cuda","cpu"}) {
    var settings=new SpeechSettings{Model=model,Device=device};var assets=await SpeechModels.EnsureAsync(settings,modelDirectory,true,downloads,progress,default);
    var transcriber=new SpeechTranscriber(settings,modelDirectory,ensure:(_,_)=>Task.FromResult(assets));
    // GPU checks cover an entire 6m40s isolated fixture; CPU checks cover a real 68s speech clip.
    string media=device=="cuda"?fixture:Path.Combine(benchmark,"clips","clip_01.wav");double seconds=device=="cuda"?duration:clips[0]!["duration"]!.GetValue<double>();int audio=device=="cuda"?1:0;
    var cache=Path.Combine(root,"cache-"+model+"-"+device);var database=new ScoutDatabase(Path.Combine(root,model+"-"+device+".db"));ScoutStorage.Register(cache,database);
    try {
        var sourceKey=CollectionResolver.Hash(JsonSerializer.Serialize(new{size=new FileInfo(media).Length,mtime=PreviewPlanner.MtimeNs(media),sample=PreviewPlanner.SampledDigest(media)}));
        var transcript=await transcriber.TranscribeAsync(new(media,seconds,audio,ffmpeg,cache,sourceKey),progress,default);
        if(transcript.Cues.Count==0||transcript.Cues.Any(c=>c.Start<0||c.Start>seconds))throw new Exception("Speech/timestamps missing.");
        Data.Save(Path.Combine(root,model+"-"+device+"-transcript.json"),transcript);
        var silence=await transcriber.TranscribeAsync(new(Path.Combine(benchmark,"clips","silence.wav"),30,0,ffmpeg,cache,"silence-v1"),progress,default);
        if(silence.Cues.Count!=0)throw new Exception("Speech hallucinated on digital silence: "+model+" "+device);
        var outcome=new JsonObject{["model"]=model,["device"]=device,["seconds"]=seconds,["cues"]=transcript.Cues.Count,["silence_empty"]=true};
        if(device=="cuda") {
            var output=Path.Combine(root,"pipeline-"+model);var pipelineDb=new ScoutDatabase(Path.Combine(root,"pipeline-"+model+".db"));ScoutStorage.Register(output,pipelineDb);
            try {
                var series=new SeriesIdentity("tvdb","999999","Speech Fixture");Episode[] guide=[new(1,1,1,"Captioned Sample","An isolated composite of benchmark clips; not a real episode assignment.")];
                var catalog=Path.Combine(output,"catalog.json");Data.Save(catalog,new{episodes=guide});
                var refs=new SubtitleReferenceSet(series,[new("fixture","Speech.Fixture.S01E01.Captioned.Sample.srt","https://example.test/fixture.zip","fixture",1,cues)]);
                using var noNetwork=new HttpClient(new NoNetwork());
                var pipeline=new MediaPipeline(new DialogueMatcher(noNetwork),subtitles:new SubtitleMatcher(new Source(refs),Path.Combine(output,"subtitles")),speech:transcriber);
                var path=await pipeline.RunAsync(new(fixtureRoot,catalog,output,"not-used","http://127.0.0.1:1234/v1/chat/completions",Ffmpeg:ffmpeg,Ffprobe:Path.Combine(Path.GetDirectoryName(ffmpeg)!,"ffprobe.exe"),SeriesIdentity:series,ContentOptions:new()),progress,default);
                var report=Data.Read(path);var group=CollectionResolver.Resolve(report).Single();outcome["pipeline_matched"]=group.Episode?.Id==1;
                if(group.Episode?.Id!=1)throw new Exception("Native speech failed the subtitle-only fixture pipeline: "+report.ToJsonString()[..Math.Min(report.ToJsonString().Length,900)]);
                if(!group.Rows.All(r=>r["transcription"]!=null&&SubtitleResult.Valid(r,guide,series)))throw new Exception("Missing or invalid speech/subtitle provenance.");
                var saved=report.ToJsonString();var refreshed=Data.Read(path);if(saved!=refreshed.ToJsonString())throw new Exception("Database persistence mismatch.");
            }finally{ScoutStorage.Unregister(output);}
        }
        results.Add(outcome);Data.Save(Path.Combine(root,"results.json"),results);
    } finally {ScoutStorage.Unregister(cache);}
}
if(before!=PreviewPlanner.SampledDigest(fixture)||File.GetLastWriteTimeUtc(fixture)!=originalMtime)throw new Exception("Fixture source changed.");
Console.WriteLine("PASS Native CPU/CUDA speech, silence and subtitle-first fixture pipelines. Source unchanged.");
sealed class InlineProgress:IProgress<string>{public void Report(string value)=>Console.WriteLine(value);}
sealed class NoNetwork:HttpMessageHandler {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)=>throw new Exception("Native fixture must not request AI or use the network.");}
sealed class Source(SubtitleReferenceSet set):ISubtitleReferenceSource {public Task<SubtitleReferenceSet> LoadAsync(SeriesIdentity s,IReadOnlyList<Episode> e,IProgress<string> p,CancellationToken ct)=>Task.FromResult(set);}
