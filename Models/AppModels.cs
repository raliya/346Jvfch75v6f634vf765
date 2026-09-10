using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TextFileProcessor.Models;

public enum JobStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled,
    Interrupted,
    Skipped
}

public sealed class DomainJob : INotifyPropertyChanged
{
    private JobStatus _status;
    private int _progress;
    private string _message = string.Empty;
    private string _outputPath = string.Empty;
    private string _databaseName = string.Empty;
    private string _databaseUser = string.Empty;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Domain { get; set; } = string.Empty;

    public string SecondValue { get; set; } = string.Empty;

    public string ConfigPath { get; set; } = string.Empty;

    public string SqlPath { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public JobStatus Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public int Progress
    {
        get => _progress;
        set => SetField(ref _progress, value);
    }

    public string Message
    {
        get => _message;
        set => SetField(ref _message, value);
    }

    public string OutputPath
    {
        get => _outputPath;
        set => SetField(ref _outputPath, value);
    }

    public string DatabaseName
    {
        get => _databaseName;
        set => SetField(ref _databaseName, value);
    }

    public string DatabaseUser
    {
        get => _databaseUser;
        set => SetField(ref _databaseUser, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;

        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class ProcessingOptions
{
    public string SourceFolder { get; init; } = string.Empty;

    public string OutputFolder { get; init; } = string.Empty;

    public string SearchText1 { get; init; } = string.Empty;

    public string SearchText2 { get; init; } = string.Empty;

    public bool IncludeAdditionalExtensions { get; init; }

    public bool ReplaceExistingFolders { get; init; }
}

public sealed class DatabaseCredentials
{
    public string Name { get; init; } = string.Empty;

    public string User { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;
}

public sealed class ProcessingResult
{
    public string FinalFolder { get; init; } = string.Empty;

    public string ConfigPath { get; init; } = string.Empty;

    public string SqlPath { get; init; } = string.Empty;

    public string StartFile { get; init; } = string.Empty;

    public DatabaseCredentials Credentials { get; init; } = new();

    public int FilesProcessed { get; init; }

    public int ReplacementCount1 { get; init; }

    public int ReplacementCount2 { get; init; }
}

public sealed class LogEntry
{
    public DateTime CreatedAt { get; init; } = DateTime.Now;

    public string Level { get; init; } = "INFO";

    public string Domain { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string DisplayTime =>
        CreatedAt.ToString("dd.MM.yyyy HH:mm:ss");
}
