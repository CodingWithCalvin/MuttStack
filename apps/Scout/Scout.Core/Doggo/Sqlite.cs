using System.IO;
using System.Runtime.InteropServices;
namespace Doggo;
// Windows ships SQLite. Keep the native wrapper small, parameterized, and independent of app runtimes.
public sealed class Sqlite : IDisposable
{
    IntPtr db;
    public Sqlite(string path, bool readOnly = false) {
        if (sqlite3_open_v2(path, out db, readOnly ? 1 : 6, IntPtr.Zero) != 0) { var message = Error; Dispose(); throw new IOException(message); }
        sqlite3_busy_timeout(db, 5000);
        Exec("PRAGMA foreign_keys=ON"); Exec("PRAGMA synchronous=FULL");
    }
    string Error => Marshal.PtrToStringUTF8(sqlite3_errmsg(db)) ?? "SQLite error";
    public List<string[]> Query(string sql, params string[] values) {
        IntPtr statement = IntPtr.Zero;
        try {
            if(sqlite3_prepare_v2(db, sql, -1, out statement, IntPtr.Zero)!=0) throw new IOException(Error);
            for(int i=0;i<values.Length;i++) if(sqlite3_bind_text(statement,i+1,values[i],-1,new IntPtr(-1))!=0) throw new IOException(Error);
            var rows=new List<string[]>();int rc;
            while((rc=sqlite3_step(statement))==100) {
                var row=new string[sqlite3_column_count(statement)];
                for(int i=0;i<row.Length;i++) row[i]=Marshal.PtrToStringUTF8(sqlite3_column_text(statement,i))??"";
                rows.Add(row);
            }
            if(rc!=101) throw new IOException(Error);
            return rows;
        } finally { if(statement!=IntPtr.Zero)sqlite3_finalize(statement); }
    }
    public void Exec(string sql, params string[] values) => Query(sql, values);
    public void Dispose(){if(db!=IntPtr.Zero){sqlite3_close_v2(db);db=IntPtr.Zero;}}
    const string Lib="winsqlite3.dll";
    [DllImport(Lib,CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)]string path,out IntPtr db,int flags,IntPtr vfs);
    [DllImport(Lib,CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_close_v2(IntPtr db);
    [DllImport(Lib,CallingConvention=CallingConvention.Cdecl)] static extern IntPtr sqlite3_errmsg(IntPtr db);
    [DllImport(Lib,CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_busy_timeout(IntPtr db,int ms);
    [DllImport(Lib,CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_prepare_v2(IntPtr db,[MarshalAs(UnmanagedType.LPUTF8Str)]string sql,int bytes,out IntPtr statement,IntPtr tail);
    [DllImport(Lib,CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_bind_text(IntPtr statement,int index,[MarshalAs(UnmanagedType.LPUTF8Str)]string text,int bytes,IntPtr destructor);
    [DllImport(Lib,CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_step(IntPtr statement);
    [DllImport(Lib,CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_finalize(IntPtr statement);
    [DllImport(Lib,CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_column_count(IntPtr statement);
    [DllImport(Lib,CallingConvention=CallingConvention.Cdecl)] static extern IntPtr sqlite3_column_text(IntPtr statement,int index);
}
