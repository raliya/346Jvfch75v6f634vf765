using System.Globalization;
using Microsoft.Data.Sqlite;
using TextFileProcessor.Models;

namespace TextFileProcessor.Data;

public sealed class AppDatabase
{
    private readonly string _connectionString;

    public string DatabasePath { get; }

    public AppDatabase()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "TextFileProcessor");

        Directory.CreateDirectory(directory);

        DatabasePath = Path.Combine(directory, "app.db");

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    public void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();

        command.CommandText =
        """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;

        CREATE TABLE IF NOT EXISTS jobs
        (
            id TEXT PRIMARY KEY,
            domain TEXT NOT NULL,
            second_value TEXT NOT NULL DEFAULT '',
            status TEXT NOT NULL,
            progress INTEGER NOT NULL DEFAULT 0,
            message TEXT NOT NULL DEFAULT '',
            output_path TEXT NOT NULL DEFAULT '',
            config_path TEXT NOT NULL DEFAULT '',
            sql_path TEXT NOT NULL DEFAULT '',
            database_name TEXT NOT NULL DEFAULT '',
            database_user TEXT NOT NULL DEFAULT '',
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS logs
        (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            created_at TEXT NOT NULL,
            level TEXT NOT NULL,
            domain TEXT NOT NULL DEFAULT '',
            message TEXT NOT NULL
        );
        """;

        command.ExecuteNonQuery();

        using var recovery = connection.CreateCommand();

        recovery.CommandText =
        """
        UPDATE jobs
        SET status = 'Interrupted',
            message = 'Выполнение было прервано закрытием программы.',
            updated_at = $updated
        WHERE status = 'Running';
        """;

        recovery.Parameters.AddWithValue(
            "$updated",
            DateTime.Now.ToString("O"));

        recovery.ExecuteNonQuery();
    }

    public void SaveJob(DomainJob job)
    {
        job.UpdatedAt = DateTime.Now;

        using var connection = Open();
        using var command = connection.CreateCommand();

        command.CommandText =
        """
        INSERT INTO jobs
        (
            id,
            domain,
            second_value,
            status,
            progress,
            message,
            output_path,
            config_path,
            sql_path,
            database_name,
            database_user,
            created_at,
            updated_at
        )
        VALUES
        (
            $id,
            $domain,
            $secondValue,
            $status,
            $progress,
            $message,
            $outputPath,
            $configPath,
            $sqlPath,
            $databaseName,
            $databaseUser,
            $createdAt,
            $updatedAt
        )
        ON CONFLICT(id) DO UPDATE SET
            domain = excluded.domain,
            second_value = excluded.second_value,
            status = excluded.status,
            progress = excluded.progress,
            message = excluded.message,
            output_path = excluded.output_path,
            config_path = excluded.config_path,
            sql_path = excluded.sql_path,
            database_name = excluded.database_name,
            database_user = excluded.database_user,
            updated_at = excluded.updated_at;
        """;

        command.Parameters.AddWithValue("$id", job.Id);
        command.Parameters.AddWithValue("$domain", job.Domain);
        command.Parameters.AddWithValue(
            "$secondValue",
            job.SecondValue);
        command.Parameters.AddWithValue(
            "$status",
            job.Status.ToString());
        command.Parameters.AddWithValue(
            "$progress",
            job.Progress);
        command.Parameters.AddWithValue(
            "$message",
            job.Message);
        command.Parameters.AddWithValue(
            "$outputPath",
            job.OutputPath);
        command.Parameters.AddWithValue(
            "$configPath",
            job.ConfigPath);
        command.Parameters.AddWithValue(
            "$sqlPath",
            job.SqlPath);
        command.Parameters.AddWithValue(
            "$databaseName",
            job.DatabaseName);
        command.Parameters.AddWithValue(
            "$databaseUser",
            job.DatabaseUser);
        command.Parameters.AddWithValue(
            "$createdAt",
            job.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue(
            "$updatedAt",
            job.UpdatedAt.ToString("O"));

        command.ExecuteNonQuery();
    }

    public List<DomainJob> LoadJobs()
    {
        var jobs = new List<DomainJob>();

        using var connection = Open();
        using var command = connection.CreateCommand();

        command.CommandText =
        """
        SELECT
            id,
            domain,
            second_value,
            status,
            progress,
            message,
            output_path,
            config_path,
            sql_path,
            database_name,
            database_user,
            created_at,
            updated_at
        FROM jobs
        ORDER BY created_at DESC
        LIMIT 500;
        """;

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            Enum.TryParse<JobStatus>(
                reader.GetString(3),
                true,
                out var status);

            jobs.Add(new DomainJob
            {
                Id = reader.GetString(0),
                Domain = reader.GetString(1),
                SecondValue = reader.GetString(2),
                Status = status,
                Progress = reader.GetInt32(4),
                Message = reader.GetString(5),
                OutputPath = reader.GetString(6),
                ConfigPath = reader.GetString(7),
                SqlPath = reader.GetString(8),
                DatabaseName = reader.GetString(9),
                DatabaseUser = reader.GetString(10),
                CreatedAt = DateTime.Parse(
                    reader.GetString(11),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                UpdatedAt = DateTime.Parse(
                    reader.GetString(12),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind)
            });
        }

        return jobs;
    }

    public void AddLog(LogEntry entry)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();

        command.CommandText =
        """
        INSERT INTO logs
        (
            created_at,
            level,
            domain,
            message
        )
        VALUES
        (
            $createdAt,
            $level,
            $domain,
            $message
        );
        """;

        command.Parameters.AddWithValue(
            "$createdAt",
            entry.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue(
            "$level",
            entry.Level);
        command.Parameters.AddWithValue(
            "$domain",
            entry.Domain);
        command.Parameters.AddWithValue(
            "$message",
            entry.Message);

        command.ExecuteNonQuery();
    }

    public List<LogEntry> LoadLogs()
    {
        var logs = new List<LogEntry>();

        using var connection = Open();
        using var command = connection.CreateCommand();

        command.CommandText =
        """
        SELECT
            created_at,
            level,
            domain,
            message
        FROM logs
        ORDER BY id DESC
        LIMIT 1000;
        """;

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            logs.Add(new LogEntry
            {
                CreatedAt = DateTime.Parse(
                    reader.GetString(0),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                Level = reader.GetString(1),
                Domain = reader.GetString(2),
                Message = reader.GetString(3)
            });
        }

        return logs;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
