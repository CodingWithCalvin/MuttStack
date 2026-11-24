using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;

static class SeasonPreviewTests
{
    public static void Run(Action<string,Action> test,string temp)
    {
        void Check(bool condition,string message="Season preview assertion failed") { if(!condition)throw new Exception(message); }
        var episode=new Episode(201,2,1,"Opening");
        var other=new Episode(301,3,1,"Elsewhere");
        Episode[] catalog=[episode,other];
        string Root(string scenario,string folder)=>Path.Combine(temp,"season-preview",scenario,folder);
        JsonObject Row(string root,string name,Episode e,string content="synthetic recording") {
            var path=Path.Combine(root,name);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,content);
            return new JsonObject {
                ["path"]=path,["matching_method"]="reasoning",["saved_evidence"]="keep original dialogue",
                ["candidates"]=new JsonArray(new JsonObject{["episode"]=JsonSerializer.SerializeToNode(e,Data.Options)}),
                ["primary_fingerprint"]=new JsonObject {
                    ["algorithm"]="full-file-sha256-v1",["sha256"]=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),
                    ["size"]=new FileInfo(path).Length,["mtime_ns"]=PreviewPlanner.MtimeNs(path),["sampled_digest"]=PreviewPlanner.SampledDigest(path)
                }
            };
        }
        JsonObject Report(params JsonObject[] rows)=>new(){["files"]=new JsonArray(rows.Select(r=>(JsonNode)r).ToArray())};
        test("Preview coverage follows the selected season without narrowing match assignments",()=>{
            var root=Root("coverage","Show");var season=Path.Combine(root,"Season 02");
            var report=Report(Row(season,"input.mkv",other));
            report["identification_scope"]=JsonSerializer.SerializeToNode(new IdentificationScope("season",season),Data.Options);
            var before=report.ToJsonString();var plan=PreviewPlanner.Create(report,root,"Show",catalog);
            Check(plan.NotIdentified.Select(e=>e.Id).SequenceEqual(new[]{episode.Id}));
            Check(plan.Items.Single().Episode?.Id==other.Id,"Selection must not bias the accepted episode or suppress its move.");
            Check(report.ToJsonString()==before,"Planning must not modify saved evidence.");
            var output=Path.Combine(temp,"coverage-html");PreviewPlanner.Write(plan,output);
            var html=ScoutStorage.ReadText(Path.Combine(output,"preview.html"));
            Check(html.Contains("Not identified in the selected scope")&&html.Contains("selected season 02"));
        });
        test("One file or custom multi-season selection does not imply missing guide episodes",()=>{
            var root=Root("custom","Show");var report=Report(Row(Path.Combine(root,"Season 02"),"input.mkv",episode));
            report["identification_scope"]=JsonSerializer.SerializeToNode(new IdentificationScope(),Data.Options);
            var plan=PreviewPlanner.Create(report,root,"Show",catalog);
            Check(plan.NotIdentified.Count==0&&plan.Items.Count==1&&plan.CoverageNote.Contains("selected files"));
            var output=Path.Combine(temp,"custom-html");PreviewPlanner.Write(plan,output);
            Check(!ScoutStorage.ReadText(Path.Combine(output,"preview.html")).Contains("<h2>Not identified"));
            var seasonRoot=Path.Combine(root,"Season 02");
            Check(PreviewPlanner.Create(report,seasonRoot,"Show",[episode,episode with {Id=202,Number=2}]).NotIdentified.Count==0);
        });
        test("Full show coverage stays complete and full season-root coverage stays within that season",()=>{
            var root=Root("full","Show");var report=Report(Row(Path.Combine(root,"Season 02"),"input.mkv",episode));
            report["identification_scope"]=JsonSerializer.SerializeToNode(new IdentificationScope("show"),Data.Options);
            Check(PreviewPlanner.Create(report,root,"Show",catalog).NotIdentified.Single()==other);
            var seasonRoot=Path.Combine(root,"Season 02");var missing=episode with {Id=202,Number=2};
            Check(PreviewPlanner.Create(report,seasonRoot,"Show",[episode,missing,other]).NotIdentified.Single()==missing);
        });
        test("Legacy season Scouts and Specials have bounded coverage; unknown file scopes stay conservative",()=>{
            var missing=episode with {Id=202,Number=2};
            var root=Root("legacy","Season 02");var report=Report(Row(root,"input.mkv",episode));
            Check(PreviewPlanner.Create(report,root,"Show",[episode,missing,other]).NotIdentified.Single()==missing);
            root=Root("legacy","Show");report=Report(Row(root,"input.mkv",episode));
            Check(PreviewPlanner.Create(report,root,"Show",catalog).NotIdentified.Count==0);
            var special=episode with {Id=1,Season=0};var next=special with {Id=2,Number=2};root=Root("legacy","Specials");
            report=Report(Row(root,"special.mkv",special));
            Check(PreviewPlanner.Create(report,root,"Show",[special,next,other]).NotIdentified.Single()==next);
        });
        test("Arbitrary or invalid folder selections never audit the entire guide",()=>{
            var root=Root("folder","Show");var report=Report(Row(root,"input.mkv",episode));
            foreach(var folder in new[]{Path.Combine(root,"Incoming"),Path.Combine(temp,"Outside","Season 03")}) {
                report["identification_scope"]=JsonSerializer.SerializeToNode(new IdentificationScope("season",folder),Data.Options);
                Check(PreviewPlanner.Create(report,root,"Show",catalog).NotIdentified.Count==0);
            }
        });
        test("Selection scope persists in workspace and invalidates a cached preview",()=>{
            var root=Root("scope-cache","Show");var report=Report(Row(root,"input.mkv",episode));var naming=new NamingOptions();
            var store=new WorkspaceStore(Path.Combine(temp,"scope-workspaces"));var workspace=store.CreateRun(root);
            workspace.SelectionScope=new("season",Path.Combine(root,"Season 02"));store.Save(workspace);
            Check(store.ReadAll().Single().SelectionScope==workspace.SelectionScope);
            report["identification_scope"]=JsonSerializer.SerializeToNode(new IdentificationScope("show"),Data.Options);
            var signature=PreviewCache.Signature(report,root,"Show",catalog,naming,null);
            var output=Path.Combine(temp,"scope-cache");PreviewPlanner.Write(PreviewPlanner.Create(report,root,"Show",catalog),output);PreviewCache.Save(output,signature,PreviewPlanner.Create(report,root,"Show",catalog));
            Check(PreviewCache.Read(output,signature)!=null);
            report["identification_scope"]=JsonSerializer.SerializeToNode(new IdentificationScope(),Data.Options);
            Check(PreviewCache.Read(output,PreviewCache.Signature(report,root,"Show",catalog,naming,null))==null);
        });
        test("A selected season folder preserves correct names without nesting another season",()=>{
            foreach(var folder in new[]{"Season 02","Season 2","Season.02","Season_02","season-02","S02"}) {
                var root=Root("preserve",folder);var row=Row(root,"Show-S02E01-Opening.mkv",episode);
                var plan=PreviewPlanner.Create(Report(row),root+Path.DirectorySeparatorChar,"Show",catalog);
                var item=plan.Items.Single();Check(item.Action=="unchanged"&&PreviewPlanner.Same(item.Destination!,row.Text("path")),folder);
                Check(PreviewPlanner.Same(plan.LibraryRoot,root));
            }
            var special=episode with {Id=1,Season=0};var specials=Root("preserve","Specials");
            Check(PreviewPlanner.Create(Report(Row(specials,"Show-S00E01-Opening.mkv",special)),specials,"Show",[special]).Items.Single().Action=="unchanged");
        });
        test("Standardizing filenames in a selected season does not rename or duplicate the source folder",()=>{
            var root=Root("format","Season 2");var row=Row(root,"Show-S02E01-Opening.mkv",episode);
            var naming=new NamingOptions{PreserveCorrectNames=false,PathTemplate="Season.{season:00}\\{series}.{code}.{title}"};
            var item=PreviewPlanner.Create(Report(row),root,"Show",catalog,naming:naming).Items.Single();
            Check(item.Action=="rename/move"&&item.Change=="Formatting only");
            Check(item.Destination==Path.Combine(root,"Show.S02E01.Opening.mkv"));
        });
        test("Season-scoped templates retain subfolders after the season component",()=>{
            foreach(var folder in new[]{"Season 02","Volume 02"}) {
                var root=Root("nested",folder);var row=Row(root,"input.mkv",episode);
                var naming=new NamingOptions{PathTemplate="{series}\\Volume {season:00}\\HD\\{code} - {title}"};
                var item=PreviewPlanner.Create(Report(row),root,"Show",catalog,naming:naming).Items.Single();
                Check(item.Destination==Path.Combine(root,"HD","S02E01 - Opening.mkv"),folder);
            }
        });
        test("Show roots and arbitrary source folders still receive the configured season path",()=>{
            foreach(var folder in new[]{"Show","Incoming","Season 02 backups"}) {
                var root=Root("show-root",folder);var row=Row(root,"input.mkv",episode);
                var plan=PreviewPlanner.Create(Report(row),root,"Show",catalog);
                Check(plan.Items.Single().Destination==Path.Combine(root,"Season 02","Show - S02E01 - Opening.mkv"));
            }
        });
        test("Season-scoped previews hold other seasons and ambiguous season templates",()=>{
            var root=Root("outside","Season 02");var row=Row(root,"input.mkv",other);
            var item=PreviewPlanner.Create(Report(row),root,"Show",catalog).Items.Single();
            Check(item.Action=="hold"&&item.Destination==null&&item.BlockedReasons.Any(r=>r.Contains("source season")));
            var same=Row(root,"same.mkv",episode);
            var naming=new NamingOptions{PathTemplate="Season {season:00}\\Season {season:00}\\{code} - {title}"};
            item=PreviewPlanner.Create(Report(same),root,"Show",catalog,naming:naming).Items.Single();
            Check(item.Action=="hold"&&item.Destination==null);
        });
        test("Season-scoped duplicates and features stay separate inside the selected folder",()=>{
            var root=Root("duplicates","Season 02");var original=Row(root,"Show-S02E01-Opening.mkv",episode);var copy=Row(root,"copy.mkv",episode);
            var feature=Row(root,"feature.mkv",episode,"interview");feature["candidates"]=new JsonArray();feature["reasoning"]=new JsonObject{["assessment"]="non_episode"};
            var items=PreviewPlanner.Create(Report(original,copy,feature),root,"Show",catalog).Items;
            Check(items.Single(i=>i.Category=="episode").Action=="unchanged");
            Check(Path.GetDirectoryName(items.Single(i=>i.Category=="duplicate").Destination)==Path.Combine(root,"_Duplicates"));
            Check(Path.GetDirectoryName(items.Single(i=>i.Category=="feature").Destination)==Path.Combine(root,"Extras"));
            Check(items.All(i=>PreviewPlanner.Within(i.Destination!,root)));
        });
        test("Old preview cache signatures are invalidated after season-scope correction",()=>{
            var root=Root("cache","Season 02");var report=Report(Row(root,"input.mkv",episode));var naming=new NamingOptions();
            var plan=PreviewPlanner.Create(report,root,"Show",catalog,naming:naming);var output=Path.Combine(temp,"season-cache");
            var old=CollectionResolver.Hash(JsonSerializer.Serialize(new{version=3,report,root=Path.GetFullPath(root),series="Show",catalog,naming,identity=(SeriesIdentity?)null},Data.Options));
            PreviewPlanner.Write(plan,output);PreviewCache.Save(output,old,plan);
            Check(PreviewCache.Read(output,PreviewCache.Signature(report,root,"Show",catalog,naming,null))==null);
        });
        test("Season-scoped moves reject the old nested preview and retain saved analysis after rename",()=>{
            var root=Root("apply","Season 02");var row=Row(root,"input.mkv",episode);var source=row.Text("path");var report=Report(row);
            var reportPath=Path.Combine(temp,"season-move-data","report.json");Data.Save(reportPath,report);
            var plan=PreviewPlanner.Create(report,root,"Show",catalog);var item=plan.Items.Single();
            var staleItem=JsonSerializer.SerializeToNode(item,Data.Options)!.AsObject();staleItem["destination"]=Path.Combine(root,"Season 02","Show - S02E01 - Opening.mkv");
            var stale=plan with {Items=[staleItem.Deserialize<PlanItem>(Data.Options)!]};
            bool rejected=false;
            try{MoveExecutor.ApplyAsync(reportPath,stale,[source],catalog,new InlineProgress(),default).GetAwaiter().GetResult();}
            catch(InvalidOperationException){rejected=true;}
            Check(rejected&&File.Exists(source)&&JsonNode.DeepEquals(Data.Read(reportPath),report));
            var moved=MoveExecutor.ApplyAsync(reportPath,plan,[source],catalog,new InlineProgress(),default).GetAwaiter().GetResult();
            var target=Path.Combine(root,"Show - S02E01 - Opening.mkv");
            Check(File.Exists(target)&&!File.Exists(source)&&!Directory.Exists(Path.Combine(root,"Season 02")));
            Check(moved.Array("files")[0].Text("path")==target&&moved.Array("files")[0].Text("saved_evidence")=="keep original dialogue");
            Check(PreviewPlanner.Create(moved,root,"Show",catalog).Items.Single().Action=="completed");
        });
    }
    private sealed class InlineProgress:IProgress<MoveUpdate> { public void Report(MoveUpdate update){} }
}
