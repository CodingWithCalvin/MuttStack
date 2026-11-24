using System.Security.Cryptography;
using System.Text.Json;
using Doggo;
using Scout.Core;
using Microsoft.Data.Sqlite;

// Synthetic contract messages only. No companion assembly, source, or private queue is loaded.
if(args.Length<2)throw new ArgumentException("Usage: <prepare|send|accept|append|verify|selection-handoff|selection-status> <isolated-root> [offline-snapshot]");
var mode=args[0];var root=Path.GetFullPath(args[1]);
if(!Path.IsPathFullyQualified(args[1])||!(root.Contains("\\.build\\handoff-rehearsal-",StringComparison.OrdinalIgnoreCase)||mode.StartsWith("selection-")&&root.Contains("\\.build\\cutover-rehearsal-",StringComparison.OrdinalIgnoreCase)))throw new ArgumentException("Use an isolated .build/handoff-rehearsal-* directory.");
for(var ancestor=new DirectoryInfo(root);ancestor!=null;ancestor=ancestor.Parent)if(ancestor.Exists&&ancestor.Attributes.HasFlag(FileAttributes.ReparsePoint))throw new IOException("Rehearsal paths cannot traverse junctions.");
var path=Path.Combine(root,"doggo.db");var marker=Path.Combine(root,"isolated-handoff-rehearsal.json");const string RunId="synthetic-database-contract-rehearsal";
void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS "+text);}
void Json(string name,object value)=>File.WriteAllText(Path.Combine(root,name),JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true}));
Dictionary<string,List<string>> Rows(string database) {
 using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=database,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());db.Open();using var c=db.CreateCommand();c.CommandText="SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
 var names=new List<string>();using(var r=c.ExecuteReader())while(r.Read())names.Add(r.GetString(0));var result=new Dictionary<string,List<string>>();
 foreach(var name in names){c.CommandText="SELECT * FROM \""+name.Replace("\"","\"\"")+"\"";using var r=c.ExecuteReader();var hashes=new List<string>();while(r.Read()){var values=Enumerable.Range(0,r.FieldCount).Select(i=>r.IsDBNull(i)?null:r.GetValue(i)).ToArray();hashes.Add(Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(values))));}hashes.Sort(StringComparer.Ordinal);result[name]=hashes;}return result;
}
string Offer(string database,string label,params int[] indices) {
 var manifest=Path.Combine(root,label+".fixture.json");
 if(!File.Exists(manifest)) {
  var files=indices.Select(i=>{var file=Path.Combine(root,"synthetic-"+i+".mkv");File.WriteAllText(file,"synthetic compressed fixture "+i);var info=new FileInfo(file);return new {JobId="fixture-"+i,Path=file,Size=info.Length,ModifiedUtc=info.LastWriteTimeUtc,Series=new{Provider="tvdb",Id=(100+i).ToString(),Name="Fixture "+i,Year="2026",Overview="Synthetic"},Season="2",Volume="1",Disc="2",DiscLabel="Fixture",RipDate=DateTime.UtcNow,OriginalFilename=info.Name};}).ToArray();
  File.WriteAllText(manifest,JsonSerializer.Serialize(new{Id=Guid.NewGuid().ToString("D"),RunId,RunName="Database contract fixture",Files=files,Version=1,SourceApp="tucker",TargetApp="scout",Contract="tucker.scout.outputs"}));
 }
 var payload=File.ReadAllText(manifest);var id=JsonDocument.Parse(payload).RootElement.GetProperty("Id").GetString()!;new RoutedHandoffs(database).Offer(id,ScoutHandoff.Digest(payload),payload);return id;
}
void Accept(string database,string workspaces) {
 var db=ScoutDatabase.OpenExistingDoggo(database);ScoutStorage.Register(workspaces,db);
 try{var inbox=new ScoutInbox(db,workspaces);foreach(var item in inbox.Items().Where(i=>i.Read().RunId==RunId)){var first=inbox.Accept(item.Id);Check(inbox.Accept(item.Id).RunIds.SequenceEqual(first.RunIds),"Acceptance is idempotent through the database contract");}Check(new WorkspaceStore(workspaces).ReadAll().All(r=>r.Status=="Draft"&&r.SelectedFiles.Count==1&&r.SelectionScope?.Kind=="files"&&r.Report==""),"Receiver creates exact-file waiting drafts without starting processing");}finally{ScoutStorage.Unregister(workspaces);}
}
if(mode.StartsWith("selection-")) {
 var choice=ScoutStorageSelection.Resolve(Path.Combine(root,"Scout"));Check(choice.UsesDoggo&&Path.GetFullPath(choice.DatabasePath)==Path.Combine(root,"Doggo","doggo.db"),"Scout selector points at the isolated shared database");
 if(mode=="selection-handoff"){Offer(choice.DatabasePath,"selected",0);Accept(choice.DatabasePath,choice.WorkspacesRoot);}else if(mode!="selection-status")throw new ArgumentException("Unknown mode.");
 Check(new RoutedHandoffs(choice.DatabasePath).Inbox("scout").Any(i=>i.Read().RunId==RunId&&i.Status=="Accepted"),"Another database client observes durable acceptance after the receiver closed");return;
}
if(mode=="prepare") {
 if(Directory.Exists(root))throw new IOException("Preparation requires a new directory.");
 string? source=null;string? hash=null;
 if(args.Length==3){source=Path.GetFullPath(args[2]);if(!source.Contains("\\.build\\doggo-rehearsal-",StringComparison.OrdinalIgnoreCase)||Path.GetFileName(source)!="doggo.migrated.db")throw new ArgumentException("Use an offline migration rehearsal snapshot.");hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));}
 Directory.CreateDirectory(root);
 if(source!=null){using var original=new SqliteConnection($"Data Source={source};Mode=ReadOnly;Pooling=False");using var copy=new SqliteConnection($"Data Source={path};Pooling=False");original.Open();copy.Open();original.BackupDatabase(copy);}
 _=new ScoutDatabase(path,doggo:true);Json("baseline.json",Rows(path));Json(Path.GetFileName(marker),new{Version=1,Source=source,SourceHash=hash});Console.WriteLine("Prepared "+path);return;
}
if(!File.Exists(marker)||!File.Exists(path))throw new IOException("Prepare an isolated rehearsal first.");
if(mode=="send"){var id=Offer(path,"first",0,1);Offer(path,"first",0,1);Check(new RoutedHandoffs(path).Inbox("scout").Count(i=>i.Id==id)==1,"A frozen fixture retry creates one batch");return;}
if(mode=="accept"){Accept(path,Path.Combine(root,"workspaces"));return;}
if(mode=="append"){Check(new RoutedHandoffs(path).Inbox("scout").Any(i=>i.Read().RunId==RunId&&i.Status=="Accepted"),"Producer observes the earlier database acceptance");Offer(path,"later",2);return;}
if(mode=="verify") {
 var baseline=JsonSerializer.Deserialize<Dictionary<string,List<string>>>(File.ReadAllText(Path.Combine(root,"baseline.json")))!;var current=Rows(path);
 foreach(var (table,hashes) in baseline)Check(current.TryGetValue(table,out var rows)&&hashes.GroupBy(h=>h).All(g=>rows.Count(h=>h==g.Key)>=g.Count()),"Existing "+table+" rows remain intact");
 var items=new RoutedHandoffs(path).Inbox("scout").Where(i=>i.Read().RunId==RunId).ToList();Check(items.Count==2&&items.All(i=>i.Status=="Accepted")&&items.Sum(i=>i.Read().Files.Count)==3,"Two batches represent three accepted fixture files");
 using var sql=new Doggo.Sqlite(path);Check(sql.Query("PRAGMA integrity_check")[0][0]=="ok"&&sql.Query("PRAGMA foreign_key_check").Count==0,"Database integrity and foreign keys pass");
 var original=JsonDocument.Parse(File.ReadAllText(marker)).RootElement;if(original.GetProperty("Source").GetString() is {} source)Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)))==original.GetProperty("SourceHash").GetString(),"Offline source snapshot is unchanged");
 Json("handoff-report.json",new{Passed=true,Database=path,ContractFixtures=true,AcceptedBatches=2,AcceptedFiles=3,LiveAppsUpdated=false});return;
}
throw new ArgumentException("Unknown mode.");
