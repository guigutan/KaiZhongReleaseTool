using Microsoft.Data.Sqlite;
using System.IO;

namespace KaiZhongReleaseTool;

/// <summary>使用独立的 log.db 保存发布和回滚日志集，与服务器配置数据库完全分离。</summary>
public sealed class LogRepository
{
    private readonly string _connectionString;
    private readonly object _syncRoot = new();

    public LogRepository(string? databasePath = null)
    {
        var path = databasePath ?? Path.Combine(AppContext.BaseDirectory, "log.db");
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        Initialize();
    }

    private void Initialize()
    {
        lock (_syncRoot)
        {
            using var connection = new SqliteConnection(_connectionString); connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS LogSets(Name TEXT PRIMARY KEY, LogType TEXT NOT NULL, CreatedAt TEXT NOT NULL, BackupFileName TEXT NOT NULL DEFAULT '');
CREATE TABLE IF NOT EXISTS LogEntries(Id INTEGER PRIMARY KEY AUTOINCREMENT, LogSetName TEXT NOT NULL, CreatedAt TEXT NOT NULL, ServerName TEXT NOT NULL DEFAULT '', Level TEXT NOT NULL, Message TEXT NOT NULL);";
            command.ExecuteNonQuery();
        }
    }

    public void CreateSet(string name, string type, string? backupFileName = null)
    {
        lock (_syncRoot)
        {
            using var connection = new SqliteConnection(_connectionString); connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO LogSets(Name, LogType, CreatedAt, BackupFileName) VALUES($name,$type,$created,$backup);";
            command.Parameters.AddWithValue("$name", name); command.Parameters.AddWithValue("$type", type); command.Parameters.AddWithValue("$created", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")); command.Parameters.AddWithValue("$backup", backupFileName ?? string.Empty); command.ExecuteNonQuery();
        }
    }

    public void Append(string setName, string message, string level, string serverName = "")
    {
        lock (_syncRoot)
        {
            using var connection = new SqliteConnection(_connectionString); connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO LogEntries(LogSetName,CreatedAt,ServerName,Level,Message) VALUES($set,$created,$server,$level,$message);";
            command.Parameters.AddWithValue("$set", setName); command.Parameters.AddWithValue("$created", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")); command.Parameters.AddWithValue("$server", serverName); command.Parameters.AddWithValue("$level", level); command.Parameters.AddWithValue("$message", message); command.ExecuteNonQuery();
        }
    }

    public List<LogSetRecord> GetSets()
    {
        lock (_syncRoot) { using var connection = new SqliteConnection(_connectionString); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT Name,LogType,CreatedAt,BackupFileName FROM LogSets ORDER BY CreatedAt DESC;"; using var reader = command.ExecuteReader(); var list = new List<LogSetRecord>(); while (reader.Read()) list.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))); return list; }
    }

    public List<LogEntryRecord> GetEntries(string setName)
    {
        lock (_syncRoot) { using var connection = new SqliteConnection(_connectionString); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT CreatedAt,ServerName,Level,Message FROM LogEntries WHERE LogSetName=$set ORDER BY Id;"; command.Parameters.AddWithValue("$set", setName); using var reader = command.ExecuteReader(); var list = new List<LogEntryRecord>(); while (reader.Read()) list.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))); return list; }
    }
}

public sealed record LogSetRecord(string Name, string LogType, string CreatedAt, string BackupFileName);
public sealed record LogEntryRecord(string CreatedAt, string ServerName, string Level, string Message);
