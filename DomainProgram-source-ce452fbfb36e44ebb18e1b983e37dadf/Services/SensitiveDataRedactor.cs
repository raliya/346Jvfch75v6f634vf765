using System.Text.RegularExpressions;

namespace TextFileProcessor.Services;

public static class SensitiveDataRedactor
{
    private static readonly Regex ConnectionPasswordRegex = new(
        @"(?i)\b(password|passwd|pwd|pass)\s*[:=]\s*[^\s,;]+",
        RegexOptions.Compiled);

    private static readonly Regex PhpPasswordRegex = new(
        @"(?i)(['""]pass['""]\s*=>\s*['""])[^'""]*(['""])",
        RegexOptions.Compiled);

    public static string Redact(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var result = ConnectionPasswordRegex.Replace(
            message,
            "$1=***");

        return PhpPasswordRegex.Replace(
            result,
            "$1***$2");
    }
}
