using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;
using Scout.Core.Subtitles;

static class SpeechTests {
    sealed class Progress:IProgress<string>{public List<string> Lines=[];public void Report(string value)=>Lines.Add(value);}
    static void Check(bool ok,string message="Speech assertion failed"){if(!ok)throw new Exception(message);}
    static void Reject(Action action){try{action();}catch(Exception ex) when(ex is ArgumentException or IOException or InvalidDataException or InvalidOperationException){return;}throw new Exception("Expected rejection");}
    sealed class Source(SubtitleReferenceSet set):ISubtitleReferenceSource {
        public Task<SubtitleReferenceSet> LoadAsync(SeriesIdentity identity,IReadOnlyList<Episode> catalog,IProgress<string> progress,CancellationToken ct)=>Task.FromResult(set);
    }
    sealed class Speech(List<Cue> cues):ISpeechTranscriber {
        public int Calls;public string ConfigurationKey{get;set;}="test-speech-v1";public Exception? Failure;public Action? After;
        public Task<SpeechTranscript> TranscribeAsync(SpeechRequest r,IProgress<string> p,CancellationToken ct,ScoutPause? pause=null) {
            Calls++;ct.ThrowIfCancellationRequested();if(Failure!=null)throw Failure;After?.Invoke();
            return Task.FromResult(new SpeechTranscript(cues,new JsonObject{["configuration"]=ConfigurationKey,["name"]="Test speech",["complete"]=true,["audio_track"]=r.AudioTrack}));
        }
    }
    public static void Run(Action<string,Action> test,string temp) {
        test("Speech settings persist independent of the episode model and reject unknown profiles",()=>{
            var store=new SettingsStore(Path.Combine(temp,"speech-settings"));var s=new AppSettings{Speech=new(){Model="whisper-turbo",Device="cuda",Enabled=false}};store.Save(s);
            Check(store.Load().Speech==s.Speech&&store.Load().LocalModel=="");
            Check(JsonSerializer.Deserialize<AppSettings>("{}",Data.Options)!.Speech.Model=="parakeet-v3");
            Reject(()=>(s.Speech with{Model="unknown"}).Validate());Reject(()=>(s.Speech with{Device="remote"}).Validate());Reject(()=>(s.Speech with{ChunkSeconds=0}).Validate());
        });
        test("Speech timestamps validate both native output formats and reject malformed or fabricated timing",()=>{
            var p=SpeechTranscriber.ParseOutput("parakeet",JsonNode.Parse("""{"text":"Hello there","words":[{"word":"Hello","start":1.0,"end":1.5},{"word":"there","start":1.5,"end":2.0}]}""")!,3);Check(p.Count==2&&p[1].Start==1.5);
            var w=SpeechTranscriber.ParseOutput("whisper",JsonNode.Parse("""{"transcription":[{"text":"Hello there","offsets":{"from":1000,"to":2000}}]}""")!,3);Check(w.Single()==new SpeechSpan(1,2,"Hello there"));
            Check(SpeechTranscriber.ParseOutput("parakeet",JsonNode.Parse("""{"text":"","words":[]}""")!,3).Count==0);
            foreach(var bad in new[]{"""{"text":"Made up","words":[]}""","""{"words":[{"word":"bad","start":2,"end":1}]}""","""{"words":[{"word":"bad","start":0,"end":99}]}""","""{"words":[{"word":"missing time"}]}"""})Reject(()=>SpeechTranscriber.ParseOutput("parakeet",JsonNode.Parse(bad)!,3));
        });
        test("English main audio is preferred over foreign defaults and commentary",()=>{
            var probe=JsonNode.Parse("""{"streams":[{"codec_type":"audio","index":1,"tags":{"language":"fra"},"disposition":{"default":1}},{"codec_type":"audio","index":2,"tags":{"language":"eng","title":"Commentary"},"disposition":{"comment":1}},{"codec_type":"audio","index":3,"tags":{"language":"eng"},"disposition":{"default":0}}]}""")!;
            Check(MediaPipeline.SelectSpeechAudio(probe)!.Int("index")==3);
        });
        test("Speech chunks resume from database cache with offsets and no duplicate overlap",()=>{
            var root=Path.Combine(temp,"speech-chunks");var cache=Path.Combine(root,"cache");var db=new ScoutDatabase(Path.Combine(root,"copy.db"));ScoutStorage.Register(cache,db);
            int calls=0,ensures=0;bool fail=true;string? wavDirectory=null;double start=0,length=0;
            Task<string> Execute(string exe,IEnumerable<string> values,CancellationToken ct) {
                var a=values.ToArray();
                if(exe=="ffmpeg") {start=double.Parse(a[Array.IndexOf(a,"-ss")+1],System.Globalization.CultureInfo.InvariantCulture);length=double.Parse(a[Array.IndexOf(a,"-t")+1],System.Globalization.CultureInfo.InvariantCulture);wavDirectory=Path.GetDirectoryName(a[^1]);File.WriteAllBytes(a[^1],new byte[64]);}
                else {
                    calls++;if(fail&&calls==2)throw new IOException("Synthetic interruption");
                    var words=Enumerable.Range(0,13).Select(i=>i*5.0).Where(t=>t>=start&&t<start+length).Select(t=>new{word="word"+t,start=t-start,end=Math.Min(length,t-start+.2)}).ToArray();
                    File.WriteAllText(a[^1],JsonSerializer.Serialize(new{text=string.Join(" ",words.Select(w=>w.word)),words}));
                }
                return Task.FromResult("");
            }
            Task<SpeechAssets> Ensure(IProgress<string> p,CancellationToken ct){ensures++;return Task.FromResult(new SpeechAssets("speech","model",null,"runtime"));}
            var settings=new SpeechSettings{ChunkSeconds=30};var req=new SpeechRequest(Path.Combine(temp,"media","episode.mkv"),65,2,"ffmpeg",cache,"fingerprint");var log=new Progress();
            try {
                Reject(()=>new SpeechTranscriber(settings,root,Execute,Ensure).TranscribeAsync(req,log,default).GetAwaiter().GetResult());
                Check(calls==2&&wavDirectory!=null&&!Directory.Exists(wavDirectory));fail=false;
                var transcriber=new SpeechTranscriber(settings,root,Execute,Ensure);var result=transcriber.TranscribeAsync(req,log,default).GetAwaiter().GetResult();
                Check(calls==4&&result.Cues.SelectMany(c=>c.Text.Split(' ')).SequenceEqual(Enumerable.Range(0,13).Select(i=>"word"+(i*5))));
                Check(result.Cues.Last().Start>=60&&result.Provenance.Int("audio_track")==2&&!Directory.Exists(cache)&&!Directory.Exists(wavDirectory));
                int before=ensures;var offline=new SpeechTranscriber(settings,root,(_,_,_)=>throw new Exception("No tool expected"),(_,_)=>throw new Exception("No assets needed for saved transcript"));
                Check(offline.TranscribeAsync(req,log,default).GetAwaiter().GetResult().Cues.SequenceEqual(result.Cues)&&ensures==before);
                transcriber.TranscribeAsync(req with{AudioTrack=3},log,default).GetAwaiter().GetResult();Check(calls==7);
                transcriber.TranscribeAsync(req with{SourceKey="changed"},log,default).GetAwaiter().GetResult();Check(calls==10);
                Check(transcriber.ConfigurationKey!=new SpeechTranscriber(settings with{Device="cuda"},root).ConfigurationKey);
                Check(transcriber.ConfigurationKey!=new SpeechTranscriber(settings with{Model="whisper-turbo"},root).ConfigurationKey);
            } finally {ScoutStorage.Unregister(cache);}
        });
        test("Speech cancellation removes temporary audio and never caches partial output",()=>{
            var cache=Path.Combine(temp,"speech-cancel-cache");string? temporary=null;using var cancel=new CancellationTokenSource();
            var t=new SpeechTranscriber(new(),temp,(exe,args,ct)=>{var a=args.ToArray();temporary=Path.GetDirectoryName(a[^1]);File.WriteAllBytes(a[^1],new byte[64]);cancel.Cancel();ct.ThrowIfCancellationRequested();return Task.FromResult("");},(_,_)=>Task.FromResult(new SpeechAssets("speech","model",null,"runtime")));
            try{t.TranscribeAsync(new(Path.Combine(temp,"media","a.mkv"),60,0,"ffmpeg",cache,"fp"),new Progress(),cancel.Token).GetAwaiter().GetResult();throw new Exception("Expected cancellation");}catch(OperationCanceledException){}
            Check(temporary!=null&&!Directory.Exists(temporary)&&!ScoutStorage.List(cache,"*.json").Any());
        });
        test("Whisper always uses speech detection and native arguments preserve literal paths",()=>{
            var s=new SpeechSettings{Model="whisper-turbo"};var args=SpeechTranscriber.Arguments(SpeechModels.Profile(s),new("exe","C:\\model path\\file.bin","vad.bin","runtime"),"C:\\a path\\audio.wav","C:\\b path\\result.json",s);
            Check(args.Contains("--vad")&&args.Contains("-ng")&&args.Contains("C:\\a path\\audio.wav")&&args.Contains("C:\\b path\\result"));
        });
        test("Speech runtime extraction rejects traversal and verifies existing files",()=>{
            var root=Path.Combine(temp,"speech-zips");Directory.CreateDirectory(root);
            string Zip(string name,string entry){var path=Path.Combine(root,name);using var zip=ZipFile.Open(path,ZipArchiveMode.Create);using var writer=new StreamWriter(zip.CreateEntry(entry).Open());writer.Write("test executable");return path;}
            var good=Zip("good.zip","nested/nemo-speech.exe");var target=Path.Combine(root,"installed");var exe=SpeechModels.InstallAsync(good,target,"nemo-speech.exe",default).GetAwaiter().GetResult();Check(File.Exists(exe));
            SpeechModels.InstallAsync(good,target,"nemo-speech.exe",default).GetAwaiter().GetResult();File.AppendAllText(exe,"tampered");Reject(()=>SpeechModels.InstallAsync(good,target,"nemo-speech.exe",default).GetAwaiter().GetResult());
            var bad=Zip("bad.zip","../nemo-speech.exe");Reject(()=>SpeechModels.InstallAsync(bad,Path.Combine(root,"bad"),"nemo-speech.exe",default).GetAwaiter().GetResult());Check(!File.Exists(Path.Combine(root,"nemo-speech.exe")));
            using var http=new HttpClient(new NoNetwork());try{SpeechModels.EnsureAsync(new(),Path.Combine(root,"missing"),false,http,new Progress(),default).GetAwaiter().GetResult();throw new Exception("Expected unavailable model");}catch(SpeechNotReadyException){}
        });
        foreach(bool preflight in new[]{false,true})test($"Speech fallback matches cached references without AI and preserves resume results (preflight={preflight})",()=>{
            var root=Path.Combine(temp,"speech-pipeline-"+preflight);Directory.CreateDirectory(root);var path=Path.Combine(root,"a.mkv");File.WriteAllText(path,"original media");
            var output=Path.Combine(temp,"speech-pipeline-out-"+preflight);var guide=Path.Combine(output,"catalog.json");var series=new SeriesIdentity("tvdb","123","Example");Episode[] episodes=[new(1,1,1,"Silver Comet","The crew follows a comet.")];Data.Save(guide,new{episodes});
            var cues=Enumerable.Range(0,180).Select(i=>new Cue(i*5,$"first{i} alpha{i} planet{i} nebula{i} captain{i} end{i}")).ToList();
            var refs=new SubtitleReferenceSet(series,[new("ref","Example.S01E01.Silver.Comet.srt","https://example.test/sub.zip","archive",1,cues)]);var speech=new Speech(cues);using var http=new HttpClient(new NoNetwork());int tools=0;bool embedded=false;
            var pipeline=new MediaPipeline(new DialogueMatcher(http), (exe,args,ct)=>{
                tools++;if(exe=="probe")return Task.FromResult(embedded?"""{"format":{"duration":"900"},"streams":[{"codec_type":"subtitle","codec_name":"subrip","index":2}]}""":"""{"format":{"duration":"900"},"streams":[{"codec_type":"audio","index":1,"tags":{"language":"eng"}}]}""");
                if(args.Contains("streamhash"))return Task.FromResult("0,v,SHA256="+new string('a',64)+"\n1,a,SHA256="+new string('b',64));
                return Task.FromResult(string.Join("\n\n",cues.Select((c,i)=>$"{i+1}\n{TimeSpan.FromSeconds(c.Start):hh\\:mm\\:ss},000 --> {TimeSpan.FromSeconds(c.Start+2):hh\\:mm\\:ss},000\n{c.Text}")));
            },new SubtitleMatcher(new Source(refs),Path.Combine(output,"subtitles")),speech);
            var settings=new MatchSettings(root,guide,output,"m","http://127.0.0.1:1234/v1/chat/completions",Ffprobe:"probe",Ffmpeg:"extract",SeriesIdentity:series,ContentOptions:preflight?new():null);var log=new Progress();
            var reportPath=pipeline.RunAsync(settings,log,default).GetAwaiter().GetResult();var report=Data.Read(reportPath);var row=report.Array("files").Single()!.AsObject();
            Check(speech.Calls==1&&SubtitleResult.Valid(row,episodes,series)&&row["transcription"].Text("name")=="Test speech");
            Check(CollectionResolver.Resolve(report).Single().Episode?.Id==1&&EvidenceReview.Create(CollectionResolver.Resolve(report).Single()).Sections.Any(s=>s.Heading=="Machine transcription"));
            Check(log.Lines.Any(l=>l.Contains("machine transcription"))&&File.ReadAllText(path)=="original media");
            var before=row["dialogue"]!.ToJsonString();speech.ConfigurationKey="changed-model";int toolCount=tools;
            pipeline.RunAsync(settings with{Restart=true},log,default).GetAwaiter().GetResult();Check(speech.Calls==1&&tools==toolCount&&Data.Read(reportPath).Array("files").Single()!["dialogue"]!.ToJsonString()==before);
            pipeline.RunAsync(settings with{Reanalyze=true},log,default).GetAwaiter().GetResult();Check(speech.Calls==2);
            embedded=true;File.AppendAllText(path," with subtitles");pipeline.RunAsync(settings with{Reanalyze=true},log,default).GetAwaiter().GetResult();Check(speech.Calls==2&&Data.Read(reportPath).Array("files").Single()!["transcription"]==null);
        });
        test("Speech completes for every recording before AI starts even without subtitle matching",()=>{
            var root=Path.Combine(temp,"speech-before-ai");Directory.CreateDirectory(root);
            foreach(var name in new[]{"one.mkv","two.mkv"})File.WriteAllText(Path.Combine(root,name),"synthetic fixture");
            var output=Path.Combine(temp,"speech-before-ai-output");var guide=Path.Combine(output,"catalog.json");
            Data.Save(guide,new{episodes=new[]{new Episode(1,1,1,"Silver Comet","The crew follows the silver comet.")}});
            var cues=Enumerable.Range(0,150).Select(i=>new Cue(i*5,$"Silver comet captain topic{i} question{i} answer{i} outcome{i}")).ToList();
            var speech=new Speech(cues);using var http=new HttpClient(new NoNetwork());using var stop=new CancellationTokenSource();int callsAtAi=0;
            var matcher=new DialogueMatcher(http,beforeRequest:ct=>{callsAtAi=speech.Calls;stop.Cancel();ct.ThrowIfCancellationRequested();return Task.CompletedTask;});
            var pipeline=new MediaPipeline(matcher,(_,args,_)=>Task.FromResult(args.Contains("streamhash")?"0,v,SHA256="+new string('a',64):"""{"format":{"duration":"900"},"streams":[{"codec_type":"audio","index":0}]}"""),speech:speech);
            var settings=new MatchSettings(root,guide,output,"fixture","http://127.0.0.1:1234/v1/chat/completions",ContentOptions:null,UseAiFallback:true);
            try{pipeline.RunAsync(settings,new Progress(),stop.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){}
            Check(callsAtAi==2&&speech.Calls==2,"AI must not retain the model worker before all speech is ready.");
        });
        test("Missing speech assets and silence are neutral while engine failures stay held",()=>{
            var root=Path.Combine(temp,"speech-errors-media");Directory.CreateDirectory(root);var file=Path.Combine(root,"a.mkv");File.WriteAllText(file,"unchanged");
            var output=Path.Combine(temp,"speech-errors-out");var guide=Path.Combine(output,"catalog.json");Data.Save(guide,new{episodes=new[]{new Episode(1,1,1,"One")}});using var http=new HttpClient(new NoNetwork());
            var speech=new Speech([]);var pipeline=new MediaPipeline(new DialogueMatcher(http),(_,_,_)=>Task.FromResult("""{"format":{"duration":"900"},"streams":[{"codec_type":"audio","index":0}]}"""),speech:speech);
            var settings=new MatchSettings(root,guide,output,"m","http://127.0.0.1:1234/v1/chat/completions",ContentOptions:new());
            foreach(var failure in new Exception?[]{null,new SpeechNotReadyException("prepare"),new IOException("engine failed")}) {
                speech.Failure=failure;var report=Data.Read(pipeline.RunAsync(settings with{Reanalyze=true},new Progress(),default).GetAwaiter().GetResult());var row=report.Array("files").Single()!.AsObject();
                Check(row.Array("candidates").Count==0&&DialogueAvailability.Unavailable(row)==(failure is not IOException||failure is SpeechNotReadyException));
                if(failure is IOException and not SpeechNotReadyException)Check(row.Text("error")=="engine failed");
            }
            speech.Failure=null;speech.After=()=>File.AppendAllText(file," changed");var changed=Data.Read(pipeline.RunAsync(settings with{Reanalyze=true},new Progress(),default).GetAwaiter().GetResult());Check(changed.Array("files").Single().Text("error").Contains("changed"));
        });
    }
    sealed class NoNetwork:HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>throw new Exception("Unexpected network/model request");
    }
}
