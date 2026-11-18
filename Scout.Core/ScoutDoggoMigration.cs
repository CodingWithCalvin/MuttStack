using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Scout.Core;

public sealed record DoggoMigrationReport(string SourceSnapshot,string Target,int Documents,int FileResults,int CredentialDocuments,string Digest,bool IntegrityVerified,bool PeerTablesUnchanged,bool LiveDatabaseSwitched=false);

public static class ScoutDoggoMigration {
 static SqliteConnection Open(string path,bool readOnly=false) {
  var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.GetFullPath(path),Mode=readOnly?SqliteOpenMode.ReadOnly:SqliteOpenMode.ReadWrite,Pooling=false,DefaultTimeout=5}.ToString());
  db.Open();using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";cmd.ExecuteNonQuery();return db;
 }
 // BackupDatabase uses SQLite's online backup API, including committed WAL pages.
 // Never copy a live .db file with File.Copy, and never open the source read/write.
 public static void Backup(string source,string destination) {
  if(Path.GetFullPath(source).Equals(Path.GetFullPath(destination),StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Backup must have a separate destination.");
  using(var file=new FileStream(destination,FileMode.CreateNew,FileAccess.Write)){}
  using var input=Open(source,true);using var output=Open(destination);input.BackupDatabase(output);
 }
 public static DoggoMigrationReport Rehearse(string source,string? sharedDatabase,string outputDirectory) {
  var directory=Path.GetFullPath(outputDirectory);
  if(Directory.Exists(directory)||File.Exists(directory))throw new IOException("Choose a new rehearsal directory; existing backups are never overwritten.");
  if(!File.Exists(source)||sharedDatabase!=null&&!File.Exists(sharedDatabase))throw new FileNotFoundException("A source database is unavailable.");
  Directory.CreateDirectory(directory);
  var snapshot=Path.Combine(directory,"scout.snapshot.db");var target=Path.Combine(directory,"doggo.migrated.db");
  Backup(source,snapshot);
  if(sharedDatabase!=null){var before=Path.Combine(directory,"doggo.before.db");Backup(sharedDatabase,before);Backup(before,target);}
  var result=ImportSnapshot(snapshot,target);
  File.WriteAllText(Path.Combine(directory,"migration-report.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
  return result;
 }
 // For an explicit offline migration only. The UI never calls this during startup.
 public static DoggoMigrationReport ImportSnapshot(string snapshot,string target) {
  if(Path.GetFullPath(snapshot).Equals(Path.GetFullPath(target),StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Import requires a separate destination.");
  // Refuse canonical per-user live locations even if called outside the rehearsal CLI.
  var liveScout=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Scout","scout.db");
  if(new[]{DoggoSchema.DefaultPath,liveScout}.Any(p=>Path.GetFullPath(target).Equals(Path.GetFullPath(p),StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("Live database cutover is not enabled. Use an isolated rehearsal copy.");
  using var source=Open(snapshot,true);Integrity(source);
  using(var version=source.CreateCommand()){version.CommandText="PRAGMA user_version";if(Convert.ToInt32(version.ExecuteScalar())!=1)throw new InvalidDataException("Unsupported source Scout database version.");}
  var before=File.Exists(target)?PeerState(target):new Dictionary<string,string>();
  var digest=Digest(source,"documents","file_results");
  _=new ScoutDatabase(target,doggo:true);
  using var db=Open(target);using var tx=db.BeginTransaction(deferred:false);using var cmd=db.CreateCommand();cmd.Transaction=tx;
  cmd.CommandText="SELECT count(*) FROM scout_imports WHERE digest=$d";cmd.Parameters.AddWithValue("$d",digest);bool imported=Convert.ToInt32(cmd.ExecuteScalar())>0;
  cmd.Parameters.Clear();cmd.CommandText="SELECT (SELECT count(*) FROM scout_documents)+(SELECT count(*) FROM scout_file_results)+(SELECT count(*) FROM scout_imports)";
  if(!imported&&Convert.ToInt32(cmd.ExecuteScalar())>0)throw new InvalidDataException("Destination already contains Scout data. Refusing to merge or overwrite it.");
  if(!imported) {
   Copy("documents","scout_documents",["path","kind","content"]);
   Copy("file_results","scout_file_results",["document","ordinal","source","payload"]);
   cmd.CommandText="INSERT INTO scout_imports VALUES($d,$s,$t)";cmd.Parameters.Clear();cmd.Parameters.AddWithValue("$d",digest);cmd.Parameters.AddWithValue("$s",Path.GetFullPath(snapshot));cmd.Parameters.AddWithValue("$t",DateTimeOffset.UtcNow.ToString("O"));cmd.ExecuteNonQuery();
  }
  if(Digest(db,"scout_documents","scout_file_results",tx)!=digest)throw new InvalidDataException("Migrated Scout data differs from the snapshot. Import rolled back.");
  Integrity(db,tx);tx.Commit();
  var after=PeerState(target);
  if(before.Any(p=>!after.TryGetValue(p.Key,out var value)||value!=p.Value))throw new InvalidDataException("An unrelated app table changed during rehearsal.");
  using var count=source.CreateCommand();count.CommandText="SELECT (SELECT count(*) FROM documents),(SELECT count(*) FROM file_results),(SELECT count(*) FROM documents WHERE kind='credentials.dpapi')";
  using var reader=count.ExecuteReader();reader.Read();return new(snapshot,target,reader.GetInt32(0),reader.GetInt32(1),reader.GetInt32(2),digest,true,true);
  void Copy(string from,string to,string[] columns) {
   using var read=source.CreateCommand();read.CommandText=$"SELECT {string.Join(',',columns)} FROM {from}";using var rows=read.ExecuteReader();
   while(rows.Read()) {cmd.CommandText=$"INSERT INTO {to}({string.Join(',',columns)}) VALUES({string.Join(',',columns.Select((_,i)=>"$"+i))})";cmd.Parameters.Clear();for(int i=0;i<columns.Length;i++)cmd.Parameters.AddWithValue("$"+i,rows.GetValue(i));cmd.ExecuteNonQuery();}
  }
 }
 static void Integrity(SqliteConnection db,SqliteTransaction? tx=null) {
  using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="PRAGMA integrity_check";if((string?)cmd.ExecuteScalar()!="ok")throw new InvalidDataException("SQLite integrity check failed.");
  cmd.CommandText="PRAGMA foreign_key_check";using var rows=cmd.ExecuteReader();if(rows.Read())throw new InvalidDataException("SQLite foreign key check failed.");
 }
 static string Digest(SqliteConnection db,string documents,string results,SqliteTransaction? tx=null)=>TableDigest(db,documents,tx)+":"+TableDigest(db,results,tx);
 static string TableDigest(SqliteConnection db,string table,SqliteTransaction? tx=null,string suffix="") {
  using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT * FROM \""+table.Replace("\"","\"\"")+"\" "+suffix;
  using var rows=cmd.ExecuteReader();var hashes=new List<string>();
  while(rows.Read()) {
   using var stream=new MemoryStream();using(var writer=new BinaryWriter(stream,Encoding.UTF8,true))for(int i=0;i<rows.FieldCount;i++) {
    var value=rows.GetValue(i);writer.Write(value.GetType().FullName!);
    var bytes=value is byte[] blob?blob:Encoding.UTF8.GetBytes(Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture)??"");writer.Write(bytes.Length);writer.Write(bytes);
   }
   hashes.Add(Convert.ToHexString(SHA256.HashData(stream.ToArray())));
  }
  hashes.Sort(StringComparer.Ordinal);return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',hashes))));
 }
 static Dictionary<string,string> PeerState(string path) {
  using var db=Open(path,true);using var cmd=db.CreateCommand();cmd.CommandText="SELECT name,sql FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT GLOB 'scout_*'";
  var tables=new List<(string Name,string Sql)>();using(var rows=cmd.ExecuteReader())while(rows.Read())tables.Add((rows.GetString(0),rows.GetString(1)));
  return tables.ToDictionary(t=>t.Name,t=>t.Sql+"\n"+TableDigest(db,t.Name,suffix:t.Name=="doggo_migrations"?"WHERE component <> 'scout'":""));
 }
}
