using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Scout.Core;

// Status-only reads. Never repairs workspaces, loads settings from the live DB,
// or starts/stops any process owned by the user's Scout.
sealed class ScoutIdleGate(string database,string endpoint) : IDisposable {
 readonly HttpClient http=new(){Timeout=TimeSpan.FromSeconds(4)};
 public async Task<(bool Idle,string Detail)> Check(CancellationToken ct) {
  int running=0;
  if(database!="") {
   // A closed WAL database can otherwise try to create -shm even in read-only
   // mode. Immutable reads are allowed only while no WAL exists; reject changes
   // across the short query so this status probe never writes to app storage.
   bool immutable=!File.Exists(database+"-wal");var before=new FileInfo(database);long length=before.Length;var modified=before.LastWriteTimeUtc;
   string source=immutable?new Uri(Path.GetFullPath(database)).AbsoluteUri+"?immutable=1":database;
   using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=source,Mode=SqliteOpenMode.ReadOnly,Pooling=false,DefaultTimeout=3}.ToString());db.Open();
   using var cmd=db.CreateCommand();cmd.CommandText="SELECT content FROM documents WHERE kind='workspace.json'";
   using var reader=cmd.ExecuteReader();while(reader.Read()) {
    var workspace=JsonNode.Parse((byte[])reader[0])!;
    if(workspace.Text("Status")=="Running"&&!workspace.Bool("Deleted"))running++;
   }
   var after=new FileInfo(database);
   if(immutable&&(File.Exists(database+"-wal")||after.Length!=length||after.LastWriteTimeUtc!=modified))return(false,"Scout database changed while reading idle status; waiting.");
  }
  bool modelIdle=false;string modelState;
  try {
   using var response=await http.GetAsync(endpoint+"/slots",ct);response.EnsureSuccessStatusCode();
   var slots=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!.AsArray();
   modelIdle=slots.Count>0&&slots.All(s=>s?["is_processing"] is JsonValue value&&value.TryGetValue<bool>(out var busy)&&!busy);
   modelState=modelIdle?"model idle":"model processing";
  }catch(HttpRequestException ex) when(ex.InnerException is SocketException socket&&socket.SocketErrorCode==SocketError.ConnectionRefused){modelIdle=true;modelState="model stopped";}
  return(running==0&&modelIdle,$"{running} running Scout(s); {modelState}");
 }
 public async Task Wait(Action<string> status,CancellationToken ct) {
  DateTimeOffset? idleSince=null;string previous="";
  while(true) {
   ct.ThrowIfCancellationRequested();(bool Idle,string Detail) state;
   try{state=await Check(ct);}catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException or SqliteException){state=(false,$"Idle status unavailable ({ex.GetType().Name}: {ex.Message}); waiting without starting inference.");}
   if(state.Detail!=previous){status(state.Detail);previous=state.Detail;}
   if(state.Idle){idleSince??=DateTimeOffset.UtcNow;if(DateTimeOffset.UtcNow-idleSince>=TimeSpan.FromSeconds(60))return;}else idleSince=null;
   await Task.Delay(TimeSpan.FromSeconds(15),ct);
  }
 }
 public void Dispose()=>http.Dispose();
}
