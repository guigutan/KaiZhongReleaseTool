using Microsoft.Data.Sqlite;
using System.IO;

namespace KaiZhongReleaseTool;

/// <summary>使用 SQLite 持久化客户端服务器配置。</summary>
public sealed class ServerRepository
{
    private readonly string _connectionString;

    public ServerRepository()
    {
        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KaiZhongReleaseTool");
        Directory.CreateDirectory(dataDirectory);
        var databasePath = Path.Combine(dataDirectory, "servers.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        Initialize();
    }

    /// <summary>首次运行时创建服务器配置表。</summary>
    private void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"CREATE TABLE IF NOT EXISTS Servers (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL,
            GroupName TEXT NOT NULL DEFAULT '',
            Host TEXT NOT NULL,
            Port INTEGER NOT NULL,
            Username TEXT NOT NULL DEFAULT '',
            Password TEXT NOT NULL DEFAULT '',
            RemoteDesktopPort INTEGER NOT NULL DEFAULT 3389,
            UNIQUE(Host, Port)
        );";
        command.ExecuteNonQuery();
        // 为旧版本已经存在的数据库补充远程桌面字段。
        AddColumnIfMissing(connection, "GroupName", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(connection, "Username", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(connection, "Password", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(connection, "RemoteDesktopPort", "INTEGER NOT NULL DEFAULT 3389");
        using var groupCommand = connection.CreateCommand();
        groupCommand.CommandText = @"CREATE TABLE IF NOT EXISTS Groups (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL UNIQUE
        );
        UPDATE Servers SET GroupName='' WHERE GroupName='默认分组';
        DELETE FROM Groups WHERE Name='默认分组';
        INSERT OR IGNORE INTO Groups(Name) SELECT DISTINCT GroupName FROM Servers WHERE GroupName <> '';";
        groupCommand.ExecuteNonQuery();
    }

    /// <summary>按名称读取全部服务器。</summary>
    public List<ServerProfile> GetAll()
    {
        var result = new List<ServerProfile>();
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, GroupName, Host, Port, Username, Password, RemoteDesktopPort FROM Servers ORDER BY GroupName, Name;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new ServerProfile
            {
                Id = reader.GetInt64(0), Name = reader.GetString(1), GroupName = reader.GetString(2), Host = reader.GetString(3), Port = reader.GetInt32(4),
                Username = reader.GetString(5), Password = reader.GetString(6), RemoteDesktopPort = reader.GetInt32(7)
            });
        return result;
    }

    /// <summary>新增服务器并返回数据库生成的编号。</summary>
    public long Add(ServerProfile server)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Servers(Name, GroupName, Host, Port, Username, Password, RemoteDesktopPort) VALUES($name, $groupName, $host, $port, $username, $password, $rdpPort); SELECT last_insert_rowid();";
        AddParameters(command, server);
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    /// <summary>保存已有服务器配置。</summary>
    public void Update(ServerProfile server)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Servers SET Name=$name, GroupName=$groupName, Host=$host, Port=$port, Username=$username, Password=$password, RemoteDesktopPort=$rdpPort WHERE Id=$id;";
        AddParameters(command, server);
        command.Parameters.AddWithValue("$id", server.Id);
        command.ExecuteNonQuery();
    }

    /// <summary>按编号删除服务器配置。</summary>
    public void Delete(long id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Servers WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>读取全部独立分组。</summary>
    public List<string> GetGroups()
    {
        var groups = new List<string>();
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Name FROM Groups ORDER BY Name;";
        using var reader = command.ExecuteReader();
        while (reader.Read()) groups.Add(reader.GetString(0));
        return groups;
    }

    /// <summary>新增一个不重名的服务器分组。</summary>
    public void AddGroup(string name)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Groups(Name) VALUES($name);";
        command.Parameters.AddWithValue("$name", name);
        command.ExecuteNonQuery();
    }

    /// <summary>重命名分组，并同步更新该组内的全部服务器。</summary>
    public void RenameGroup(string oldName, string newName)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE Groups SET Name=$newName WHERE Name=$oldName; UPDATE Servers SET GroupName=$newName WHERE GroupName=$oldName;";
        command.Parameters.AddWithValue("$oldName", oldName);
        command.Parameters.AddWithValue("$newName", newName);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>删除未被服务器使用的分组。</summary>
    public void DeleteGroup(string name)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM Servers WHERE GroupName=$name;";
        countCommand.Parameters.AddWithValue("$name", name);
        if ((long)(countCommand.ExecuteScalar() ?? 0L) > 0)
            throw new InvalidOperationException("该分组仍有服务器，请先把服务器调整到其他分组。 ");
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Groups WHERE Name=$name;";
        command.Parameters.AddWithValue("$name", name);
        command.ExecuteNonQuery();
    }

    /// <summary>使用参数化 SQL 写入字段，避免服务器名称影响 SQL 语句。</summary>
    private static void AddParameters(SqliteCommand command, ServerProfile server)
    {
        command.Parameters.AddWithValue("$name", server.Name);
        command.Parameters.AddWithValue("$groupName", server.GroupName);
        command.Parameters.AddWithValue("$host", server.Host);
        command.Parameters.AddWithValue("$port", server.Port);
        command.Parameters.AddWithValue("$username", server.Username);
        command.Parameters.AddWithValue("$password", server.Password);
        command.Parameters.AddWithValue("$rdpPort", server.RemoteDesktopPort);
    }

    /// <summary>数据库升级时，仅在字段不存在的情况下执行 ALTER TABLE。</summary>
    private static void AddColumnIfMissing(SqliteConnection connection, string columnName, string definition)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA table_info(Servers);";
        using var reader = check.ExecuteReader();
        var exists = false;
        while (reader.Read())
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
        reader.Close();
        if (exists) return;
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE Servers ADD COLUMN {columnName} {definition};";
        alter.ExecuteNonQuery();
    }
}
