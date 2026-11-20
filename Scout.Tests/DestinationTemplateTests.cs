using System.Text.Json;
using System.Text.Json.Nodes;
using Scout.Core;

static class DestinationTemplateTests {
 public static void Run(Action<string,Action> test,string temp) {
  void Check(bool yes,string message="Destination template assertion failed"){if(!yes)throw new Exception(message);}
  void Reject(Action action){try{action();}catch(ArgumentException){return;}throw new Exception("Expected destination rejection");}
  var identity=new SeriesIdentity("tvdb","82493","iCarly","2007");
  test("Global destinations expand show tokens and literal braces without episode context",()=>{
   Check(DestinationTemplate.Resolve(@"E:\TV\{series} ({year}) {{{provider}-{id}}}","Ignored",identity)==@"E:\TV\iCarly (2007) {tvdb-82493}");
   Check(DestinationTemplate.Resolve(@"E:\TV\{series.replaceWhitespace(""."").lowercase()}","",identity with{Name="My Show"})==@"E:\TV\my.show");
   Check(DestinationTemplate.Resolve(@"\\nas.example.test\TV\{series}","",identity)==@"\\nas.example.test\TV\iCarly");
   Check(DestinationTemplate.Resolve("", "",null)==null&&DestinationTemplate.Resolve(@"E:\Fixed  Folder\{{Show}}","",null)==@"E:\Fixed  Folder\{Show}");
  });
  test("Missing metadata is explicit and destination syntax can be saved before selecting a show",()=>{
   DestinationTemplate.Validate(@"E:\TV\{series} ({year}) {{{provider}-{id}}}");
   foreach(var token in new[]{"year","provider","id"})Reject(()=>DestinationTemplate.Resolve(@"E:\TV\{"+token+"}","Show"));
   Reject(()=>DestinationTemplate.Resolve(@"E:\TV\{year}","",identity with{Year=""}));
   Check(DestinationTemplate.Resolve(@"E:\TV\{series}","Imported Show")==@"E:\TV\Imported Show");
  });
  test("Destination validation rejects relative, traversal, device, malformed and episode-specific templates",()=>{
   foreach(var template in new[]{@"TV\{series}",@"E:TV\{series}",@"E:\TV\..\{series}",@"E:\TV\\{series}",@"\\?\E:\TV\{series}",@"\\.\share\{series}",@"\\..\share\{series}",@"E:\{season:00}",@"E:\{title}",@"E:\{unknown}",@"E:\{series",@"E:\{series.unknown()}",@"E:\TV|Other\{series}",@"E:\CON\{series}",@"E:\TV.\{series}",@"\\{provider}\share\{series}"})Reject(()=>DestinationTemplate.Validate(template));
   Check(DestinationTemplate.Resolve(@"E:\TV\{series}","..\\..\\Other:Show")==@"E:\TV\Other Show");
   Check(DestinationTemplate.Resolve(@"E:\TV\{series}","CON")==@"E:\TV\_CON");
   Check(DestinationTemplate.Resolve(@"E:\TV\{series.replace(""i"", ""../"")}","",identity)!.StartsWith(@"E:\TV\",StringComparison.Ordinal));
  });
  test("Global inheritance, custom overrides and source-folder choices remain independent",()=>{
   var settings=new AppSettings{DestinationTemplate=@"E:\TV\{series}"};var run=new RunWorkspace();
   Check(DestinationTemplate.Mode(run)=="default"&&DestinationTemplate.ForRun(settings,run)==settings.DestinationTemplate);
   run.DestinationMode="custom";run.DestinationTemplateOverride=@"F:\Archive\{series} ({year})";
   settings.DestinationTemplate=@"H:\TV\{series}";Check(DestinationTemplate.ForRun(settings,run)==run.DestinationTemplateOverride);
   run.DestinationMode="source";Check(DestinationTemplate.ForRun(settings,run)=="");
   run.DestinationMode="default";Check(DestinationTemplate.ForRun(settings,run)==settings.DestinationTemplate);
   run.DestinationMode="custom";run.DestinationTemplateOverride="";Reject(()=>DestinationTemplate.ForRun(settings,run));
  });
  test("New runs explicitly persist global default independently of existing source choices and overrides",()=>{
   var source=Path.Combine(temp,"new-destination-source");Directory.CreateDirectory(source);var store=new WorkspaceStore(Path.Combine(temp,"new-destination-runs"));
   foreach(var mode in new[]{"source","custom"}) {
    var old=store.CreateRun(source);old.DestinationMode=mode;old.DestinationTemplateOverride=mode=="custom"?@"F:\Old\{series}":null;store.Save(old);
    var created=store.CreateRun(source);var saved=store.ReadAll().Single(r=>r.Id==created.Id);
    Check(saved.DestinationMode=="default"&&saved.DestinationRoot==""&&saved.DestinationTemplateOverride==null);
    Check(store.ReadAll().Single(r=>r.Id==old.Id).DestinationMode==mode);
   }
  });
  test("Legacy destination paths preserve literal braces and exact folder spacing",()=>{
   var settings=new AppSettings{DestinationTemplate=@"E:\TV\{series}"};
   var run=JsonSerializer.Deserialize<RunWorkspace>("""{"DestinationRoot":"F:\\My  Library\\iCarly {tvdb-82493}"}""")!;
   Check(DestinationTemplate.Mode(run)=="custom"&&DestinationTemplate.Resolve(DestinationTemplate.ForRun(settings,run),"",identity)==run.DestinationRoot);
  });
  test("Global templates and run overrides survive isolated Doggo storage reopen",()=>{
   var folder=Path.Combine(temp,"destination-settings");var dbPath=Path.Combine(temp,"destination-settings.db");ScoutStorage.Register(folder,new ScoutDatabase(dbPath,doggo:true));
   try {
    var settings=new SettingsStore(Path.Combine(folder,"settings"));settings.Save(new(){ModelDirectory=temp,DestinationTemplate=@"E:\TV\{series} ({year})"});
    var store=new WorkspaceStore(Path.Combine(folder,"runs"));var first=new RunWorkspace{DestinationMode="custom",DestinationTemplateOverride=@"F:\Shows\{series}"};var second=new RunWorkspace{DestinationMode="source"};store.Save(first);store.Save(second);
    ScoutStorage.Unregister(folder);ScoutStorage.Register(folder,ScoutDatabase.OpenExistingDoggo(dbPath));
    var loaded=settings.Load();var runs=store.ReadAll();Check(loaded.DestinationTemplate==@"E:\TV\{series} ({year})"&&DestinationTemplate.ForRun(loaded,runs.Single(r=>r.Id==first.Id))==first.DestinationTemplateOverride&&DestinationTemplate.ForRun(loaded,runs.Single(r=>r.Id==second.Id))=="");
   }finally{ScoutStorage.Unregister(folder);}
  });
  test("Resolved default changes invalidate cached previews while overrides remain stable",()=>{
   var settings=new AppSettings{DestinationTemplate=@"E:\TV\{series}"};var run=new RunWorkspace{SeriesIdentity=identity};var report=new JsonObject{["files"]=new JsonArray()};
   string Signature()=>PreviewCache.Signature(report,temp,identity.Name,[],new(),identity,DestinationTemplate.Resolve(DestinationTemplate.ForRun(settings,run),identity.Name,identity));
   var before=Signature();settings.DestinationTemplate=@"F:\TV\{series}";Check(before!=Signature());
   run.DestinationMode="custom";run.DestinationTemplateOverride=@"G:\Special\{series}";before=Signature();settings.DestinationTemplate=@"H:\TV\{series}";Check(before==Signature());
  });
 }
}
