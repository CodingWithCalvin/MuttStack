using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
namespace Doggo;

// Database contract reader owned by this app. Never reads another app's settings or credential files.
public static class ScoutCredentialReader
{
    public static (string Key,string? Pin) ReadTvdb(string databasePath)=>Read(databasePath,"tvdb");
    public static string ReadTmdb(string databasePath)=>Read(databasePath,"tmdb").Key;
    public static (string Key,string? Pin) Read(string databasePath,string provider)
    {
        if(provider is not ("tvdb" or "tmdb"))throw new ArgumentException("Choose TMDB or TVDB.",nameof(provider));
        if(!Path.IsPathFullyQualified(databasePath)||!File.Exists(databasePath))throw new FileNotFoundException("Choose the shared Doggo database containing saved metadata credentials.",databasePath);
        using var lease=StorageLease.Acquire(false,databasePath);
        using var db=new Sqlite(databasePath,readOnly:true);
        foreach(var component in new[]{"doggo","scout"})
            if(db.Query("SELECT MAX(version) FROM doggo_migrations WHERE component=?",component)[0][0]!="1")throw new InvalidDataException("The shared credential database schema is not supported by this app.");
        var rows=db.Query("SELECT hex(content) FROM scout_documents WHERE kind='credentials.dpapi' LIMIT 2");
        if(rows.Count!=1||rows[0][0].Length is 0 or >2097152)throw new IOException("Doggo must contain one authoritative credential document. Save metadata credentials in Scout using this shared database.");
        var encrypted=Convert.FromHexString(rows[0][0]);
        var input=new Blob{Size=encrypted.Length,Data=Marshal.AllocHGlobal(encrypted.Length)};Blob output=default;byte[]? plain=null;
        try {
            Marshal.Copy(encrypted,0,input.Data,encrypted.Length);
            if(!CryptUnprotectData(ref input,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output))throw new CryptographicException("Windows could not unlock the shared metadata credentials for this account.");
            plain=new byte[output.Size];Marshal.Copy(output.Data,plain,0,plain.Length);
            var keys=JsonSerializer.Deserialize<Dictionary<string,string>>(plain);
            if(keys?.TryGetValue("metadata|"+provider,out var key)==true&&!string.IsNullOrWhiteSpace(key))return(key,provider=="tvdb"?keys.GetValueOrDefault("metadata|tvdb-pin"):null);
            throw new IOException($"No {provider.ToUpperInvariant()} credential is configured in Doggo. Save the {(provider=="tmdb"?"API Read Access Token":"key")} in Scout Settings using the shared database.");
        } finally {
            if(plain!=null)CryptographicOperations.ZeroMemory(plain);
            Marshal.Copy(new byte[encrypted.Length],0,input.Data,encrypted.Length);Marshal.FreeHGlobal(input.Data);
            if(output.Data!=IntPtr.Zero){Marshal.Copy(new byte[output.Size],0,output.Data,output.Size);LocalFree(output.Data);}
        }
    }
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll",SetLastError=true)] static extern bool CryptUnprotectData(ref Blob input,IntPtr description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
}
