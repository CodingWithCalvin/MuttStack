using System.Text.Json.Nodes;
using Scout.Core;

static class DialogueAvailabilityTests {
    sealed class Progress : IProgress<string> {
        public List<string> Lines = [];
        public void Report(string value) => Lines.Add(value);
    }
    public static void Run(Action<string,Action> test,string temp) {
        void Check(bool value,string message="Dialogue availability assertion failed") { if(!value)throw new Exception(message); }
        foreach(bool preflight in new[]{false,true}) test($"Missing and empty subtitles are neutral and the batch continues (preflight={preflight})",()=>{
            var root=Path.Combine(temp,"dialogue-input-"+preflight);Directory.CreateDirectory(root);
            string[] names=["missing.mkv","bitmap.mkv","empty.mkv","broken.mkv","readable.mkv"];
            foreach(var name in names)File.WriteAllText(Path.Combine(root,name),name);
            var output=Path.Combine(temp,"dialogue-output-"+preflight);var guide=Path.Combine(output,"catalog.json");
            Data.Save(guide,new{episodes=new[]{new Episode(1,1,1,"Opening","The crew discovers a new planet and investigates its mysteries.")}});
            var extracted=new List<string>();int modelStarts=0;
            using var http=new HttpClient(new FakeHandler());
            var matcher=new DialogueMatcher(http,beforeRequest:_=>{modelStarts++;throw new InvalidOperationException("Synthetic model failure");});
            var pipeline=new MediaPipeline(matcher,(exe,args,_)=>{
                var a=args.ToArray();var name=Path.GetFileName(exe=="probe"?a[^1]:a[Array.IndexOf(a,"-i")+1]);
                if(exe=="probe")return Task.FromResult(name switch {
                    "missing.mkv"=>"""{"format":{"duration":"900"},"streams":[]}""",
                    "bitmap.mkv"=>"""{"format":{"duration":"900"},"streams":[{"codec_type":"subtitle","codec_name":"dvd_subtitle","index":2}]}""",
                    _=>"""{"format":{"duration":"900"},"streams":[{"codec_type":"subtitle","codec_name":"subrip","index":2}]}"""
                });
                Check(!a.Contains("streamhash"),"No hash or move is needed for unidentified recordings");extracted.Add(name);
                if(name=="broken.mkv")throw new IOException("Synthetic ffmpeg failure");
                return Task.FromResult(name=="empty.mkv"?"":string.Join("\n\n",Enumerable.Range(0,40).Select(i=>$"{i+1}\n00:{i:00}:00,000 --> 00:{i:00}:02,000\nScene {i} crew finds a mysterious planet and travels to investigate the signal.")));
            });
            var logs=new Progress();var settings=new MatchSettings(root,guide,output,"test-model","http://127.0.0.1:1234/v1/chat/completions",Ffmpeg:"extract",Ffprobe:"probe",ContentOptions:preflight?new():null);
            var report=Data.Read(pipeline.RunAsync(settings,logs,default).GetAwaiter().GetResult());var rows=report.Array("files").OfType<JsonObject>().ToList();
            var skipped=rows.Where(DialogueAvailability.Unavailable).ToList();
            Check(skipped.Count==3&&skipped.All(r=>r.Text("status")=="not_processed"&&r.Text("error")==""&&r["primary_fingerprint"]==null&&CompilationEvidence.SourceUnchanged(r)));
            Check(rows.Count(r=>r.Text("status")=="error")==2&&modelStarts==1,"Real tool/model failures must remain visible, and readable files must still reach matching");
            Check(extracted.Count==3&&!extracted.Contains("missing.mkv")&&!extracted.Contains("bitmap.mkv"));
            Check(logs.Lines.Count(l=>l.StartsWith("Not processed:"))==3&&!logs.Lines.Any(l=>l.Contains("Speech transcription")));
            Check(IssueReview.Select(report).Count==2&&IssueReview.Select(report,checkSources:false).Count==2);
            var plan=PreviewPlanner.Create(report,root,"Example",Data.Catalog(guide));
            Check(plan.Items.Count(i=>i.Category=="not_processed"&&i.Action=="hold"&&i.Destination==null&&i.BlockedReasons.Count==0)==3);
            Check(names.All(n=>File.ReadAllText(Path.Combine(root,n))==n),"Input media must stay unchanged");
            var changed=skipped.First();File.AppendAllText(changed.Text("path")," added subtitles");
            Check(IssueReview.Select(report).Contains(changed.Text("path")),"Changed sources must become eligible for a retry");
            Check(!IssueReview.Select(report,checkSources:false).Contains(changed.Text("path")));
        });
        test("Older subtitle-only errors display neutrally without altering saved evidence",()=>{
            foreach(var error in new[]{"No readable text subtitles. Speech transcription is not yet available in the .NET build.","No usable dialogue."}) {
                var row=new JsonObject{["path"]=Path.Combine(temp,"legacy-no-dialogue.mkv"),["error"]=error,["status"]="error",["candidates"]=new JsonArray()};
                var report=new JsonObject{["files"]=new JsonArray(row)};var before=report.ToJsonString();
                var group=CollectionResolver.Resolve(report).Single();var evidence=EvidenceReview.Create(group);
                Check(group.NotProcessed&&group.Episode==null&&group.Claims.Count==0&&group.Category=="Not processed");
                Check(!evidence.Assessment.Contains("error")&&!evidence.Sections.Any(s=>s.Heading=="Processing error"||s.Text.Contains("Speech transcription")));
                Check(IssueReview.Select(report).Count==0&&report.ToJsonString()==before);
                row["source_changed"]=true;Check(CollectionResolver.Resolve(report).Single().Status=="source_or_model_error");row.Remove("source_changed");
                row["relocation_error"]="Source moved";Check(CollectionResolver.Resolve(report).Single().Status=="source_or_model_error");row.Remove("relocation_error");
                row["error"]="ffprobe failed: unreadable input";Check(CollectionResolver.Resolve(report).Single().Status=="source_or_model_error");
            }
        });
        test("An all-skipped run completes without loading the model and explicit reanalysis retries it",()=>{
            var root=Path.Combine(temp,"all-skipped-input");Directory.CreateDirectory(root);var path=Path.Combine(root,"missing.mkv");File.WriteAllText(path,"synthetic");
            var output=Path.Combine(temp,"all-skipped-output");var guide=Path.Combine(output,"catalog.json");Data.Save(guide,new{episodes=new[]{new Episode(1,1,1,"Opening")}});
            using var http=new HttpClient(new FakeHandler());int probes=0;
            var pipeline=new MediaPipeline(new DialogueMatcher(http,beforeRequest:_=>throw new Exception("Must not load the model")),(_,_,_)=>{probes++;return Task.FromResult("""{"format":{"duration":"900"},"streams":[]}""");});
            var settings=new MatchSettings(root,guide,output,"model","http://127.0.0.1:1234/v1/chat/completions",ContentOptions:new());
            var reportPath=pipeline.RunAsync(settings,new Progress(),default).GetAwaiter().GetResult();
            pipeline.RunAsync(settings with{ReviewIssues=true},new Progress(),default).GetAwaiter().GetResult();Check(probes==1);
            pipeline.RunAsync(settings with{Reanalyze=true},new Progress(),default).GetAwaiter().GetResult();Check(probes==2);
            Check(Data.Read(reportPath).Array("files").Single().Text("status")=="not_processed");
        });
    }
}
