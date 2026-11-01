using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace CodexCleaner.Engine;

internal static class Db
{
    private const int CantOpen = 14;

    public static SqliteConnection Open(string file, bool writable)
    {
        try { return Connect(file, writable); }
        catch (SqliteException e) when (!writable && e.SqliteErrorCode == CantOpen)
        {
            // Some WAL databases cannot be opened read-only without creating sidecars.
            // Inspect a verified stable private copy instead; never create files in the source.
            foreach (var suffix in new[] { "-wal", "-journal" })
                if (File.Exists(file + suffix) && new FileInfo(file + suffix).Length > 0) throw;
            return OpenCopy(file);
        }
    }

    private static SqliteConnection Connect(string file, bool writable)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Mode = writable ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 1,
        }.ToString());
        try
        {
            connection.Open();
            Exec(connection, "PRAGMA busy_timeout=1000");
            if (!writable) Exec(connection, "PRAGMA query_only=ON");
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private static SqliteConnection OpenCopy(string file)
    {
        var directory = Directory.CreateTempSubdirectory("codex-cleaner-inspect-").FullName;
        var copy = Path.Combine(directory, "inspection.sqlite");
        try
        {
            var before = SHA256.HashData(File.ReadAllBytes(file));
            File.Copy(file, copy);
            if (!SHA256.HashData(File.ReadAllBytes(copy)).AsSpan().SequenceEqual(before) || !SHA256.HashData(File.ReadAllBytes(file)).AsSpan().SequenceEqual(before))
                throw new CleanerException("数据库正在变化，请退出 Codex 后重新检查。");
            var connection = Connect(copy, false);
            connection.Disposed += (_, _) => Directory.Delete(directory, true);
            if (Scalar(connection, "PRAGMA quick_check") as string != "ok") { connection.Dispose(); throw new CleanerException("数据库副本一致性校验未通过。"); }
            return connection;
        }
        catch
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            throw;
        }
    }

    public static string SchemaHash(SqliteConnection db)
    {
        var lines = Rows(db, "SELECT type,name,sql FROM sqlite_schema WHERE type IN ('table','trigger') AND name NOT LIKE 'sqlite_%'")
            .Select(r => (Key: $"{r[0]}:{r[1]}", Line: $"{r[0]}:{r[1]}:{((string)r[2]!).Replace("\r\n", "\n").Trim()}"))
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => x.Line);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
    }

    public static int Exec(SqliteConnection db, string sql, IReadOnlyList<object>? args = null)
    {
        using var command = Command(db, sql, args);
        return command.ExecuteNonQuery();
    }

    public static object? Scalar(SqliteConnection db, string sql, IReadOnlyList<object>? args = null)
    {
        using var command = Command(db, sql, args);
        return command.ExecuteScalar();
    }

    public static long Count(SqliteConnection db, string table, string where, IReadOnlyList<object> args) =>
        (long)Scalar(db, $"SELECT count(*) FROM {Quote(table)} WHERE {where}", args)!;

    public static List<object?[]> Rows(SqliteConnection db, string sql, IReadOnlyList<object>? args = null)
    {
        using var command = Command(db, sql, args);
        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    public static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    // Placeholders are named $p0..$pN in the order the arguments are listed.
    public static string In(int offset, int count) => string.Join(',', Enumerable.Range(offset, count).Select(i => "$p" + i));

    private static SqliteCommand Command(SqliteConnection db, string sql, IReadOnlyList<object>? args)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < (args?.Count ?? 0); i++) command.Parameters.AddWithValue("$p" + i, args![i]);
        return command;
    }
}
