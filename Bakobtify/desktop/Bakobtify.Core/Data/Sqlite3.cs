using System.Runtime.InteropServices;
using System.Text;

namespace Bakobtify.Data;

/// <summary>
/// Acesso ao SQLite que já vem no Windows (<c>winsqlite3.dll</c>).
/// Nenhum pacote NuGet: só P/Invoke.
/// </summary>
internal static class SqliteNative
{
    private const string Dll = "winsqlite3.dll";

    public const int Ok = 0;
    public const int Row = 100;
    public const int Done = 101;

    public const int OpenReadWrite = 0x00000002;
    public const int OpenCreate = 0x00000004;
    public const int OpenFullMutex = 0x00010000;

    /// <summary>SQLITE_TRANSIENT: o SQLite copia o valor imediatamente.</summary>
    public static readonly IntPtr Transient = new(-1);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_open_v2")]
    public static extern int OpenV2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_close_v2")]
    public static extern int CloseV2(IntPtr db);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_errmsg")]
    public static extern IntPtr ErrMsg(IntPtr db);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_extended_errcode")]
    public static extern int ExtendedErrCode(IntPtr db);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_busy_timeout")]
    public static extern int BusyTimeout(IntPtr db, int milliseconds);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_exec")]
    public static extern int Exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr arg, out IntPtr errMsg);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_free")]
    public static extern void Free(IntPtr pointer);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_prepare_v2")]
    public static extern int PrepareV2(IntPtr db, byte[] sql, int byteCount, out IntPtr stmt, IntPtr tail);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_step")]
    public static extern int Step(IntPtr stmt);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_reset")]
    public static extern int Reset(IntPtr stmt);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_clear_bindings")]
    public static extern int ClearBindings(IntPtr stmt);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_finalize")]
    public static extern int Finalize(IntPtr stmt);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_bind_null")]
    public static extern int BindNull(IntPtr stmt, int index);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_bind_int64")]
    public static extern int BindInt64(IntPtr stmt, int index, long value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_bind_double")]
    public static extern int BindDouble(IntPtr stmt, int index, double value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_bind_text")]
    public static extern int BindText(IntPtr stmt, int index, byte[] value, int byteCount, IntPtr destructor);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_count")]
    public static extern int ColumnCount(IntPtr stmt);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_type")]
    public static extern int ColumnType(IntPtr stmt, int index);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_int64")]
    public static extern long ColumnInt64(IntPtr stmt, int index);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_double")]
    public static extern double ColumnDouble(IntPtr stmt, int index);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_text")]
    public static extern IntPtr ColumnText(IntPtr stmt, int index);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_bytes")]
    public static extern int ColumnBytes(IntPtr stmt, int index);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_changes")]
    public static extern int Changes(IntPtr db);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_last_insert_rowid")]
    public static extern long LastInsertRowId(IntPtr db);

    public static byte[] Utf8z(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var buffer = new byte[bytes.Length + 1];
        bytes.CopyTo(buffer, 0);
        return buffer;
    }

    public static string ErrorMessage(IntPtr db)
    {
        var ptr = ErrMsg(db);
        return ptr == IntPtr.Zero ? "erro desconhecido do SQLite" : Marshal.PtrToStringUTF8(ptr) ?? "";
    }
}

public sealed class SqliteException : Exception
{
    public int Code { get; }

    public SqliteException(int code, string message) : base(message) => Code = code;
}

public sealed class SqliteConnection : IDisposable
{
    private IntPtr _db;
    private readonly object _gate = new();

    public string Path { get; }

    private SqliteConnection(IntPtr db, string path)
    {
        _db = db;
        Path = path;
    }

    public static SqliteConnection Open(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);

        var flags = SqliteNative.OpenReadWrite | SqliteNative.OpenCreate | SqliteNative.OpenFullMutex;
        var rc = SqliteNative.OpenV2(SqliteNative.Utf8z(path), out var db, flags, IntPtr.Zero);
        if (rc != SqliteNative.Ok)
        {
            var message = db != IntPtr.Zero ? SqliteNative.ErrorMessage(db) : $"código {rc}";
            if (db != IntPtr.Zero) SqliteNative.CloseV2(db);
            throw new SqliteException(rc, "Falha ao abrir o banco: " + message);
        }

        SqliteNative.BusyTimeout(db, 5000);
        return new SqliteConnection(db, path);
    }

    public void Execute(string sql)
    {
        lock (_gate)
        {
            var rc = SqliteNative.Exec(_db, SqliteNative.Utf8z(sql), IntPtr.Zero, IntPtr.Zero, out var err);
            if (rc == SqliteNative.Ok) return;

            var message = err != IntPtr.Zero ? Marshal.PtrToStringUTF8(err) : SqliteNative.ErrorMessage(_db);
            if (err != IntPtr.Zero) SqliteNative.Free(err);
            throw new SqliteException(rc, message ?? "erro no SQLite");
        }
    }

    public int Changes
    {
        get
        {
            lock (_gate) return SqliteNative.Changes(_db);
        }
    }

    public long LastInsertRowId
    {
        get
        {
            lock (_gate) return SqliteNative.LastInsertRowId(_db);
        }
    }

    public SqliteStatement Prepare(string sql)
    {
        lock (_gate)
        {
            var rc = SqliteNative.PrepareV2(_db, SqliteNative.Utf8z(sql), -1, out var stmt, IntPtr.Zero);
            if (rc != SqliteNative.Ok)
            {
                throw new SqliteException(rc, SqliteNative.ErrorMessage(_db));
            }

            return new SqliteStatement(this, stmt);
        }
    }

    internal object Gate => _gate;

    internal IntPtr Handle => _db;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_db == IntPtr.Zero) return;
            SqliteNative.CloseV2(_db);
            _db = IntPtr.Zero;
        }
    }
}

public sealed class SqliteStatement : IDisposable
{
    private readonly SqliteConnection _connection;
    private IntPtr _stmt;

    internal SqliteStatement(SqliteConnection connection, IntPtr stmt)
    {
        _connection = connection;
        _stmt = stmt;
    }

    public SqliteStatement Bind(int index, string? value)
    {
        var rc = value is null
            ? SqliteNative.BindNull(_stmt, index)
            : SqliteNative.BindText(_stmt, index, SqliteNative.Utf8z(value), -1, SqliteNative.Transient);
        Check(rc);
        return this;
    }

    public SqliteStatement Bind(int index, long value)
    {
        Check(SqliteNative.BindInt64(_stmt, index, value));
        return this;
    }

    public SqliteStatement Bind(int index, double value)
    {
        Check(SqliteNative.BindDouble(_stmt, index, value));
        return this;
    }

    /// <summary>Retorna true quando há linha; false quando terminou.</summary>
    public bool Step()
    {
        var rc = SqliteNative.Step(_stmt);
        if (rc == SqliteNative.Row) return true;
        if (rc == SqliteNative.Done) return false;
        throw new SqliteException(rc, SqliteNative.ErrorMessage(_connection.Handle));
    }

    public bool IsNull(int index) => SqliteNative.ColumnType(_stmt, index) == 5;

    public string? GetString(int index)
    {
        var ptr = SqliteNative.ColumnText(_stmt, index);
        if (ptr == IntPtr.Zero) return null;
        var length = SqliteNative.ColumnBytes(_stmt, index);
        return Marshal.PtrToStringUTF8(ptr, length);
    }

    public long GetInt64(int index) => SqliteNative.ColumnInt64(_stmt, index);

    public double GetDouble(int index) => SqliteNative.ColumnDouble(_stmt, index);

    public void Reset()
    {
        SqliteNative.Reset(_stmt);
        SqliteNative.ClearBindings(_stmt);
    }

    private void Check(int rc)
    {
        if (rc != SqliteNative.Ok)
        {
            throw new SqliteException(rc, SqliteNative.ErrorMessage(_connection.Handle));
        }
    }

    public void Dispose()
    {
        if (_stmt == IntPtr.Zero) return;
        SqliteNative.Finalize(_stmt);
        _stmt = IntPtr.Zero;
    }
}
