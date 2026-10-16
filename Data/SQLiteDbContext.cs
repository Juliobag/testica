using System.IO;
using System.Data.SQLite;

namespace Testica.Data;

public sealed class SQLiteDbContext
{
    public const string DefaultDatabasePath = @"C:\ictb\ctb.sqlite";

    private readonly string _connectionString;

    static SQLiteDbContext()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    public SQLiteDbContext(string databasePath = DefaultDatabasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        _connectionString = new SQLiteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Version = 3,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();
    }

    public string DatabasePath { get; }

    public SQLiteConnection CreateConnection()
    {
        var connection = new SQLiteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();

        return connection;
    }
}