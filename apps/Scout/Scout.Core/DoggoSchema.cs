using Microsoft.Data.Sqlite;
namespace Scout.Core;

// Prefixes are ownership conventions. Scout consumes doggo infrastructure only;
// it leaves the legacy Ripley-to-Tucker channel and peer migrations alone.
// ScoutInbox separately initializes the recipient-aware handoff channel.
public static class DoggoSchema {
 public static string DefaultPath=>System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Doggo","doggo.db");
 internal static void Initialize(SqliteConnection db) {
  using var tx=db.BeginTransaction(deferred:false);using var cmd=db.CreateCommand();cmd.Transaction=tx;
  cmd.CommandText="CREATE TABLE IF NOT EXISTS doggo_migrations(component TEXT NOT NULL,version INTEGER NOT NULL,applied_utc TEXT NOT NULL,PRIMARY KEY(component,version))";cmd.ExecuteNonQuery();
  foreach(var component in new[]{"doggo","scout"}) {
   cmd.CommandText="SELECT COALESCE(MAX(version),0) FROM doggo_migrations WHERE component=$c";cmd.Parameters.Clear();cmd.Parameters.AddWithValue("$c",component);
   var version=Convert.ToInt32(cmd.ExecuteScalar());if(version>1)throw new InvalidDataException($"The {component} database component needs a newer Scout. Existing data was kept.");
   if(version==1)continue;
   cmd.Parameters.Clear();
   if(component=="scout") {
    cmd.CommandText="CREATE TABLE scout_documents(path TEXT PRIMARY KEY COLLATE NOCASE,kind TEXT NOT NULL,content BLOB NOT NULL); CREATE TABLE scout_file_results(document TEXT NOT NULL COLLATE NOCASE,ordinal INTEGER NOT NULL,source TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(document,ordinal),FOREIGN KEY(document) REFERENCES scout_documents(path) ON DELETE CASCADE); CREATE INDEX scout_file_source ON scout_file_results(source); CREATE TABLE scout_imports(digest TEXT PRIMARY KEY,source_path TEXT NOT NULL,imported_utc TEXT NOT NULL);";cmd.ExecuteNonQuery();
   }
   cmd.CommandText="INSERT INTO doggo_migrations VALUES($c,1,$t)";cmd.Parameters.AddWithValue("$c",component);cmd.Parameters.AddWithValue("$t",DateTimeOffset.UtcNow.ToString("O"));cmd.ExecuteNonQuery();
  }
  tx.Commit();cmd.Transaction=null;cmd.CommandText="PRAGMA journal_mode=WAL";cmd.Parameters.Clear();cmd.ExecuteScalar();
 }
}
