using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Doggo;
static class DatabaseCredentialTests
{
 public static int Run(string parent) {
  int passed=0;var root=Path.Combine(parent,"database-credentials");Directory.CreateDirectory(root);var target=Path.Combine(root,"doggo.db");
  void Check(bool ok,string message){if(!ok)throw new Exception(message);passed++;Console.WriteLine("PASS "+message);}
  void Reject(Action action,string message){try{action();}catch(Exception e)when(e is IOException or InvalidDataException or CryptographicException or JsonException){Check(true,message);return;}throw new Exception(message);}
  _=new DoggoInbox(target);using(var sql=new Sqlite(target)){sql.Exec("INSERT INTO doggo_migrations VALUES('scout',1,'test')");sql.Exec("CREATE TABLE scout_documents(path TEXT PRIMARY KEY,kind TEXT,content BLOB)");}
  void Key(string? value,string? pin=null,string? tmdb=null){var keys=new Dictionary<string,string>();if(value!=null)keys["metadata|tvdb"]=value;if(pin!=null)keys["metadata|tvdb-pin"]=pin;if(tmdb!=null)keys["metadata|tmdb"]=tmdb;var encrypted=Protect(JsonSerializer.SerializeToUtf8Bytes(keys));using var sql=new Sqlite(target);sql.Exec("INSERT OR REPLACE INTO scout_documents VALUES('authoritative','credentials.dpapi',X'"+Convert.ToHexString(encrypted)+"')");}
  Key("shared-test-key","test-pin");var before=SHA256.HashData(File.ReadAllBytes(target));
  Check(ScoutCredentialReader.ReadTvdb(target)==("shared-test-key","test-pin")&&before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(target))),"Credential lookup reads only the shared database without changing it");
  Key("shared-test-key","test-pin","tmdb-test-token");before=SHA256.HashData(File.ReadAllBytes(target));
  Check(ScoutCredentialReader.ReadTmdb(target)=="tmdb-test-token"&&before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(target))),"TMDB token is read without altering the shared database");
  Check(ScoutCredentialReader.Read(target,"tmdb").Pin==null&&ScoutCredentialReader.ReadTvdb(target).Pin=="test-pin","Provider credentials remain separate");
  Key("shared-test-key","test-pin");Reject(()=>ScoutCredentialReader.ReadTmdb(target),"TMDB never falls back to a TVDB credential");
  File.WriteAllText(Path.Combine(root,"storage.json"),"invalid private Scout file");File.WriteAllText(Path.Combine(root,"credentials.dpapi"),"stale private credentials");
  Check(ScoutCredentialReader.ReadTvdb(target).Key=="shared-test-key","Private Scout configuration and credential files are never consulted");
  Reject(()=>ScoutCredentialReader.ReadTvdb(root),"A Scout settings directory cannot be used as a credential database");
  Key("rotated","new-pin");Check(ScoutCredentialReader.ReadTvdb(target)==("rotated","new-pin"),"Rotated shared keys and PINs are visible without restart");
  Key(null);Reject(()=>ScoutCredentialReader.ReadTvdb(target),"Removed shared keys never fall back to another file");Key("restored");
  using(var sql=new Sqlite(target))sql.Exec("INSERT INTO scout_documents SELECT 'ambiguous',kind,content FROM scout_documents");
  Reject(()=>ScoutCredentialReader.ReadTvdb(target),"Ambiguous credential documents require explicit repair instead of choosing a stale key");
  using(var sql=new Sqlite(target))sql.Exec("DELETE FROM scout_documents WHERE path='ambiguous'");
  using(StorageLease.Acquire(true,target))Reject(()=>ScoutCredentialReader.ReadTvdb(target),"Credential lookup respects exclusive database maintenance");
  File.Move(target,target+".offline");Reject(()=>ScoutCredentialReader.ReadTvdb(target),"An unavailable database cannot create an empty replacement or read stale credentials");File.Move(target+".offline",target);
  using(var sql=new Sqlite(target))sql.Exec("INSERT INTO doggo_migrations VALUES('scout',2,'future')");Reject(()=>ScoutCredentialReader.ReadTvdb(target),"Unsupported database credential schemas are rejected");
  return passed;
 }
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)] static extern bool CryptProtectData(ref Blob input, [MarshalAs(UnmanagedType.LPWStr)] string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    static byte[] Protect(byte[] bytes)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) }; Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            if (!CryptProtectData(ref input, "isolated test", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)) throw new CryptographicException("Test credential protection failed.");
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally { Marshal.Copy(new byte[bytes.Length], 0, input.Data, bytes.Length); Marshal.FreeHGlobal(input.Data); CryptographicOperations.ZeroMemory(bytes); if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
    }
}
