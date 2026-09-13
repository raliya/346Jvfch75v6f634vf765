namespace TextFileProcessor.Models;

public sealed class IspmanagerOperationResult
{
    public bool Success { get; init; }

    public string Message { get; init; } = string.Empty;

    public string DiagnosticPath { get; init; } = string.Empty;
}
