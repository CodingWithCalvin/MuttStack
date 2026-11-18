using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

sealed class SavedSubtitleStore:IDisposable {
 readonly SqliteConnection db;
 public SavedSubtitleStore(string root) {
  db=new(new SqliteConnectionStringBuilder{DataSource=Path.Combine(Path.GetFullPath(root),"benchmark.db"),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());db.Open();
 }
 public List<(string Path,JsonNode Value)> ReadAll() {
  using var cmd=db.CreateCommand();cmd.CommandText="SELECT path,content FROM documents WHERE path LIKE '%.json' ORDER BY path";
  using var reader=cmd.ExecuteReader();var rows=new List<(string,JsonNode)>();
  while(reader.Read())rows.Add((reader.GetString(0),JsonNode.Parse((byte[])reader[1])!));
  return rows;
 }
 public void Dispose()=>db.Dispose();
}
