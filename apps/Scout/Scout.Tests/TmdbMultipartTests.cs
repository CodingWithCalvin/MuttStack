using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;
using Scout.Core.Subtitles;

static class TmdbMultipartTests
{
    static void Check(bool ok,string message="TMDB/multipart check failed") {if(!ok)throw new Exception(message);}
    static void Reject(Action action) {try{action();}catch(Exception ex) when(ex is ArgumentException or InvalidDataException or InvalidOperationException){return;}throw new Exception("Expected rejection");}
    public static void Run(Action<string,Action> test,string temp) {
        var series=new SeriesIdentity("tmdb","5371","iCarly","2007");
        Episode[] parts=[new(101,4,11,"iParty With Victorious (1)","The first story begins."),new(102,4,12,"iParty With Victorious (2)","The second installment continues."),new(103,4,13,"iParty With Victorious (3)","The final installment concludes.")];
        List<Cue> Cues(string tag)=>Enumerable.Range(0,160).Select(i=>new Cue(i*5,$"{tag}{i} galaxy{i} captain{i} planet{i} {tag}final{i} orbit{i}")).ToList();
        var cues=Cues("party");
        SubtitleReference Reference(string name)=>new(CollectionResolver.Hash(name),name,"https://dl.subdl.com/subtitle/fixture.zip","fixture-archive",4,cues);
        SubtitleAttempt Match(string key,SubtitleReference reference,JsonObject? reviews=null,SeriesIdentity? identity=null) {
            var selected=identity??series;var matcher=new SubtitleMatcher(new Source(new(selected,[reference])),Path.Combine(temp,"tmdb-subtitle",key));
            if(reviews!=null)matcher.UseReviewedReferences(reviews);
            return matcher.IdentifyAsync(cues,parts,selected,new Progress(),default).GetAwaiter().GetResult();
        }
        JsonObject FileRow(SubtitleAttempt attempt) {
            var media=Path.Combine(temp,"tmdb-media");Directory.CreateDirectory(media);
            var file=Path.Combine(media,"reviewed-range.mkv");File.WriteAllText(file,"synthetic recording");
            var row=SubtitleResult.Row(cues,parts,attempt);row["path"]=file;row["size"]=new FileInfo(file).Length;row["source_mtime_ns"]=PreviewPlanner.MtimeNs(file);row["sampled_digest"]=PreviewPlanner.SampledDigest(file);
            row["duration"]=2400.0;row["primary_fingerprint"]=new JsonObject{["algorithm"]="primary-packets-sha256-v1",["video"]=new string('a',64),["audio"]=new string('b',64),["size"]=new FileInfo(file).Length,["mtime_ns"]=PreviewPlanner.MtimeNs(file),["sampled_digest"]=PreviewPlanner.SampledDigest(file)};
            return row;
        }
        test("New metadata defaults use TMDB and saved TVDB identity stays TVDB",()=>{
            Check(new AppSettings().MetadataProvider=="tmdb"&&new ScoutSetup().MetadataProvider=="tmdb");
            var saved=new RunWorkspace{SeriesIdentity=new("tvdb","82493","iCarly")};Check(saved.RestoreSetup().MetadataProvider=="tvdb");
        });
        test("TMDB finds the original series through bidirectionally verified TVDB identity",()=>{
            using var handler=new Handler(request=>{
                Check(request.RequestUri!.Host=="api.themoviedb.org"&&request.Headers.Authorization?.Parameter=="fixture-token");
                return request.RequestUri.AbsolutePath.EndsWith("external_ids")?"""{"id":5371,"tvdb_id":82493}""":"""{"tv_results":[{"id":5371,"name":"iCarly","first_air_date":"2007-09-08"}]}""";
            });
            using var http=new HttpClient(handler);var catalog=new MetadataCatalog(http,"tmdb","fixture-token");
            Check(catalog.FindTvdbAsync("82493",default).GetAwaiter().GetResult()==series);
            using var badHttp=new HttpClient(new Handler(_=>"""{"tv_results":[{"id":5371,"name":"iCarly"}],"id":5371,"tvdb_id":999}"""));
            Reject(()=>new MetadataCatalog(badHttp,"tmdb","fixture-token").FindTvdbAsync("82493",default).GetAwaiter().GetResult());
        });
        test("Three-episode naming uses the last episode and one shared story title",()=>{
            var naming=new NamingOptions{PathTemplate="{series} {{{provider}-{id}}}\\Season {season:00}\\{series}.{code}.{titles.join(\" and \").replaceWhitespace(\".\")}"};
            Check(naming.AssignmentName("iCarly",parts,identity:series)=="iCarly.S04E11-E13.iParty.With.Victorious");
            Check(naming.Folder("iCarly",parts[0],series)=="iCarly {tmdb-5371}\\Season 04");
            Reject(()=>naming.AssignmentName("iCarly",[parts[0],parts[2]],identity:series));
            Reject(()=>naming.AssignmentName("iCarly",[parts[0],parts[1] with{Season=5}],identity:series));
            Reject(()=>naming.AssignmentName("iCarly",[parts[0],parts[0]],identity:series));
        });
        test("Joined episode labels work only with a verified title and consecutive range",()=>{
            var link=TitleMapping.Find("iCarly.S04E11E12E13.iParty.With.Victorious.WEBRip.srt",parts,"ref");
            Check(link?.Complete==true&&link.Episodes.Length==3);
            Check(TitleMapping.Find("iCarly.S04E11E12E13.WEBRip.srt",parts,"ref")==null);
            Check(TitleMapping.Find("iCarly.S04E11E13.iParty.With.Victorious.WEBRip.srt",parts,"ref")==null);
        });
        test("Three-episode subtitle proof survives resolver and exact move preview",()=>{
            var attempt=Match("titled",Reference("iCarly.S04E11E12E13.iParty.With.Victorious.WEBRip.srt"));var row=FileRow(attempt);
            Check(SubtitleResult.Valid(row,parts,series)&&MultipartEvidence.Combined(row));
            var report=new JsonObject{["series_identity"]=JsonSerializer.SerializeToNode(series,Data.Options),["files"]=new JsonArray(row)};
            var group=CollectionResolver.Resolve(report).Single();Check(group.Combined&&group.Episodes.Count==3&&group.Code=="S04E11-E13");
            var plan=PreviewPlanner.Create(report,Path.Combine(temp,"tmdb-media"),"iCarly",parts,identity:series,destinationRoot:Path.Combine(temp,"tmdb-output"));
            Check(plan.Items.Single().Destination!.Contains("S04E11-E13")&&plan.NotIdentified.Count==0);
        });
        test("Number-only subtitle references require explicit review; undo expires derived matches",()=>{
            var reference=Reference("iCarly.S04E11E12E13.WEBRip.srt");var unmatched=Match("reviewed",reference);
            Check(unmatched.Result==null);var row=FileRow(unmatched);
            var report=new JsonObject{["series_identity"]=JsonSerializer.SerializeToNode(series,Data.Options),["files"]=new JsonArray(row)};
            Check(ReviewedReferenceMapping.CanReview(row));
            var id=HumanResolution.Apply(report,row,parts,parts,"Verified the full three-part reference.");
            ReviewedReferenceMapping.Save(report,row,parts,parts,id);
            var accepted=Match("reviewed",reference,report);Check(accepted.Result!=null);
            SubtitleResult.Apply(row,parts,accepted);Check(SubtitleResult.Valid(row,parts,series));
            var independent=row.DeepClone().AsObject();independent["path"]="Independent model result";independent["matching_method"]="reasoning";
            independent["reasoning"]=new JsonObject{["assessment"]="supported",["summary"]="Independently verified by the model"};report.Array("files").Add(independent);
            var other=Match("other-provider",reference,report,new("tvdb","82493","iCarly"));Check(other.Result==null);
            var changed=reference with{Cues=cues.Select(c=>c with{Text=c.Text+" replacement"}).ToList()};
            Check(Match("changed-reference",changed,report).Result==null);
            HumanResolution.Undo(report,id);Check(row.Array("candidates").Count==0&&row["reasoning"]==null);
            Check(independent["reasoning"].Text("summary")=="Independently verified by the model"&&independent.Array("candidates").Count==3,"Undo must preserve independent model results.");
            Check(Match("reviewed",reference,report).Result==null,"An undone review cannot survive the comparison cache.");
        });
        test("Multipart human overrides retain every claim and resolve conflicts per episode",()=>{
            var row=FileRow(Match("manual",Reference("iCarly.S04E11E12E13.WEBRip.srt")));
            var report=new JsonObject{["series_identity"]=JsonSerializer.SerializeToNode(series,Data.Options),["files"]=new JsonArray(row)};
            var id=HumanResolution.Apply(report,row,parts,parts,"Confirmed full story");
            var group=CollectionResolver.Resolve(report).Single();Check(group.Episodes.Count==3&&group.Combined&&group.HumanDecision!=null);
            HumanResolution.Undo(report,id);Check(CollectionResolver.Resolve(report).Single().Episodes.Count==0);
            Reject(()=>HumanResolution.Apply(report,row,[parts[0],parts[2]],parts,"Gap"));
        });
        test("Conflicting reference reviews stay held until the obsolete review is undone",()=>{
            var reference=Reference("iCarly.S04E11E12E13.WEBRip.srt");
            var row=FileRow(Match("conflicts",reference));
            var report=new JsonObject{["series_identity"]=JsonSerializer.SerializeToNode(series,Data.Options),["files"]=new JsonArray(row)};
            var first=HumanResolution.Apply(report,row,parts,parts,"");ReviewedReferenceMapping.Save(report,row,parts,parts,first);
            var second=HumanResolution.Apply(report,row,[parts[0]],parts,"");ReviewedReferenceMapping.Save(report,row,[parts[0]],parts,second);
            Check(Match("conflicts",reference,report).Result==null);
            HumanResolution.Undo(report,second);Check(Match("conflicts",reference,report).Result!=null);
        });
        test("Grounded three-episode coverage requires ordered complete evidence for every member",()=>{
            Cue[] dialogue=Enumerable.Range(0,6).Select(i=>new Cue(i*40,$"Distinct guide event number {i} occurs in this original dialogue.")).ToArray();var blocks=DialogueMatcher.Blocks(dialogue);
            var raw=new JsonObject{["candidates"]=new JsonArray(parts.Select((episode,i)=>(JsonNode)new JsonObject{
                ["candidate"]=i,["content_type"]="complete_episode",["reason"]="The original dialogue supports this installment.",["story_blocks"]=new JsonArray(i*2,i*2+1),
                ["statements"]=new JsonArray(new JsonObject{["statement"]=0,["verdict"]="supported",["evidence"]=new JsonArray(new JsonObject{["block"]=i*2,["quote"]=dialogue[i*2].Text},new JsonObject{["block"]=i*2+1,["quote"]=dialogue[i*2+1].Text})})}).ToArray()),
                ["recording"]=new JsonObject{["kind"]="continuous_parts",["candidates"]=new JsonArray(0,1,2),["reason"]="Three complete installments occur successively in the recording."}};
            var result=GroundedRecording.Expand(raw,blocks,parts);Check(result.Array("matches").Count==3&&result.Text("coverage")=="combined");
            var incomplete=raw.DeepClone().AsObject();incomplete["candidates"]![2]!["content_type"]="incomplete_episode";Reject(()=>GroundedRecording.Expand(incomplete,blocks,parts));
            var omitted=raw.DeepClone().AsObject();omitted["recording"]!["candidates"]=new JsonArray(0,1);Reject(()=>GroundedRecording.Expand(omitted,blocks,parts));
            var reversed=raw.DeepClone().AsObject();reversed["recording"]!["candidates"]=new JsonArray(2,1,0);Reject(()=>GroundedRecording.Expand(reversed,blocks,parts));
        });
    }
    sealed class Source(SubtitleReferenceSet set):ISubtitleReferenceSource {public Task<SubtitleReferenceSet> LoadAsync(SeriesIdentity series,IReadOnlyList<Episode> catalog,IProgress<string> progress,CancellationToken ct)=>Task.FromResult(set);}
    sealed class Progress:IProgress<string>{public void Report(string value){}}
    sealed class Handler(Func<HttpRequestMessage,string> reply):HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(reply(request))});
    }
}
