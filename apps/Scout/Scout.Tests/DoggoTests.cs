using Microsoft.Data.Sqlite;
using Scout.Core;
using System.Text;
using System.Text.Json.Nodes;
static class DoggoTests {
 static void Check(bool value){if(!value)throw new Exception("Doggo assertion failed.");}
 static void Reject(Action action){try{action();}catch(InvalidDataException){return;}throw new Exception("Unsafe database change accepted.");}
 static void Exec(string path,string sql){using var db=new SqliteConnection($"Data Source={path};Pooling=False");db.Open();using var cmd=db.CreateCommand();cmd.CommandText=sql;cmd.ExecuteNonQuery();}
 public static void Run(Action<string,Action> test,string temp) {
  test("Doggo rehearsal includes committed WAL preserves encrypted bytes virtual paths and report rows",()=>{
   var source=Path.Combine(temp,"wal-source.db");var legacy=new ScoutDatabase(source);var logical=Path.Combine(temp,"virtual-source");
   using var keeper=new SqliteConnection($"Data Source={source};Pooling=False");keeper.Open();using(var cmd=keeper.CreateCommand()){cmd.CommandText="PRAGMA wal_autocheckpoint=0";cmd.ExecuteNonQuery();}
   var report=Path.Combine(logical,"report.json");var credential=Path.Combine(logical,"credentials.dpapi");var encrypted=WindowsSecrets.Protect(Encoding.UTF8.GetBytes("{\"test\":\"test-key\"}"));
   using(var write=keeper.CreateCommand()){write.CommandText="INSERT INTO documents VALUES($p,'credentials.dpapi',$b)";write.Parameters.AddWithValue("$p",credential);write.Parameters.AddWithValue("$b",encrypted);write.ExecuteNonQuery();}
   var original=JsonNode.Parse("""{"files":[{"path":"M:\\tv\\sample.mkv","error":"interrupted","dialogue":{"cues":[{"start":2,"text":"original"}]}}],"complete":false}""")!;
   legacy.Write(report,Encoding.UTF8.GetBytes(original.ToJsonString()));Check(new FileInfo(source+"-wal").Length>32);
   var shared=Path.Combine(temp,"peers.db");_=new ScoutDatabase(shared,true);Exec(shared,"CREATE TABLE tucker_settings(id TEXT PRIMARY KEY,json TEXT); INSERT INTO tucker_settings VALUES('settings','unchanged'); INSERT INTO doggo_migrations VALUES('tucker',99,'test'); PRAGMA user_version=99;");
   var migrated=ScoutDoggoMigration.Rehearse(source,shared,Path.Combine(temp,"rehearsal"));Check(migrated.Documents==2&&migrated.FileResults==1&&migrated.CredentialDocuments==1&&migrated.PeerTablesUnchanged&&!migrated.LiveDatabaseSwitched);
   var db=ScoutDatabase.OpenExistingDoggo(migrated.Target);Check(db.Read(credential).SequenceEqual(encrypted));Check(JsonNode.DeepEquals(JsonNode.Parse(db.Read(report)),original));Check(db.List(logical,"report.json").Single()==report);
   ScoutStorage.Register(logical,db);try{Check(new SettingsStore(logical).ReadKey("test")=="test-key");}finally{ScoutStorage.Unregister(logical);}
   Check(ScoutDoggoMigration.ImportSnapshot(migrated.SourceSnapshot,migrated.Target).Digest==migrated.Digest);
   db.Write(Path.Combine(logical,"new.json"),"{}"u8.ToArray());Reject(()=>ScoutDoggoMigration.ImportSnapshot(migrated.SourceSnapshot,migrated.Target));Check(db.Exists(Path.Combine(logical,"new.json")));
   Check(legacy.Read(credential).SequenceEqual(encrypted)&&!legacy.Exists(Path.Combine(logical,"new.json")));
  });
  test("Doggo rejects unsupported consumed components but ignores unrelated apps",()=>{
   var standalone=Path.Combine(temp,"not-doggo.db");_=new ScoutDatabase(standalone);Reject(()=>ScoutDatabase.OpenExistingDoggo(standalone));
   foreach(var component in new[]{"doggo","scout"}) {
    var path=Path.Combine(temp,"future-"+component+".db");Exec(path,$"CREATE TABLE doggo_migrations(component TEXT,version INTEGER,applied_utc TEXT,PRIMARY KEY(component,version)); INSERT INTO doggo_migrations VALUES('{component}',2,'test');");
    Reject(()=>new ScoutDatabase(path,true));using var db=new SqliteConnection($"Data Source={path};Pooling=False");db.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT count(*) FROM sqlite_master WHERE name='scout_documents'";Check(Convert.ToInt32(cmd.ExecuteScalar())==0);
   }
  });
  test("Doggo isolated CRUD settings previews and archive history survive reopen",()=>{
   var path=Path.Combine(temp,"scout-doggo.db");var root=Path.Combine(temp,"doggo-virtual");var media=Path.Combine(temp,"doggo-media");Directory.CreateDirectory(media);
   ScoutStorage.Register(root,new ScoutDatabase(path,true));try {
    var store=new WorkspaceStore(root);var one=store.CreateRun(media);var two=store.CreateRun(media);one.Status="Ready for review";store.Save(one);
    store.ArchiveRun(one);Check(store.ReadAll().Single().Id==two.Id&&store.ReadAll(includeArchived:true).Count==2);
    ScoutStorage.Register(root,new ScoutDatabase(path,true));Check(store.ReadAll(includeArchived:true).Single(r=>r.Id==one.Id).Archived);
    store.DeleteRun(one);Check(store.ReadAll(includeArchived:true).Count==1);store.RestoreRun(one);Check(store.ReadAll().Single().Id==two.Id);
    store.ArchiveRun(one,false);Check(store.ReadAll().Count==2);one.Status="Running";
    try{store.ArchiveRun(one);throw new Exception("Archived a running Scout");}catch(InvalidOperationException){}Check(!one.Archived);
    try{store.DeleteRun(one);throw new Exception("Deleted a running Scout");}catch(InvalidOperationException){}Check(!one.Deleted);
    var settings=new SettingsStore(root);settings.Save(new AppSettings{LocalModel="kept"});Check(settings.Load().LocalModel=="kept");Check(!Directory.Exists(root));
   }finally{ScoutStorage.Unregister(root);}
  });
 }
}
