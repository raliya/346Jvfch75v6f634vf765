using System.Text;
using System.Text.RegularExpressions;

namespace TextFileProcessor.Services;

internal sealed record DeploymentDbConfig(
    string Host, string Name, string User, string Password)
{
    // Лексический разбор: комментарии и строковые литералы не смешиваются.
    private static readonly Regex Tokens = new(
        """
        /\*[\s\S]*?\*/|//[^\r\n]*|\#[^\r\n]*|'(?:\\[\s\S]|[^'\\])*'|"(?:\\[\s\S]|[^"\\])*"|=>|\S
        """,
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(3));

    internal static DeploymentDbConfig Read(string php)
    {
        var tokens = Tokens.Matches(php)
            .Cast<Match>()
            .Select(m => m.Value)
            .Where(t => !t.StartsWith("/*", StringComparison.Ordinal) &&
                        !t.StartsWith("//", StringComparison.Ordinal) &&
                        !t.StartsWith("#", StringComparison.Ordinal))
            .ToList();

        var positions = new List<int>();

        for (var i = 0; i + 2 < tokens.Count; i++)
        {
            if ((tokens[i] == "'db'" || tokens[i] == "\"db\"") &&
                tokens[i + 1] == "=>" &&
                tokens[i + 2] == "[")
            {
                positions.Add(i + 3);
            }
        }

        if (positions.Count != 1)
            throw new InvalidOperationException(
                "Ожидался ровно один литеральный массив 'db' => [...]. " +
                "PHP-код не выполнялся.");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var p = positions[0];

        while (true)
        {
            if (p >= tokens.Count)
                throw new InvalidOperationException("Массив db не закрыт.");

            if (tokens[p] == "]")
                break;

            var key = Decode(tokens[p++]);

            if (p >= tokens.Count || tokens[p++] != "=>")
                throw new InvalidOperationException("Некорректный элемент массива db.");

            if (p >= tokens.Count)
                throw new InvalidOperationException("В массиве db отсутствует значение.");

            var value = Decode(tokens[p++]);

            if (!values.TryAdd(key, value))
                throw new InvalidOperationException(
                    "Повторяющийся ключ в массиве db: " + key);

            if (p >= tokens.Count)
                throw new InvalidOperationException("Массив db не закрыт.");

            if (tokens[p] == "]")
                break;

            if (tokens[p++] != ",")
                throw new InvalidOperationException(
                    "В db поддерживаются только строковые литералы. " +
                    "Конкатенация, переменные и функции не вычисляются.");
        }

        string Required(string key)
        {
            if (!values.TryGetValue(key, out var value) ||
                string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException(
                    "В массиве db отсутствует непустой параметр: " + key);
            return value;
        }

        var result = new DeploymentDbConfig(
            Required("host"), Required("name"),
            Required("user"), Required("pass"));

        // В полученной схеме зарегистрирован локальный MySQL (native).
        if (result.Host != "localhost" && result.Host != "127.0.0.1")
            throw new InvalidOperationException(
                "db.host не соответствует локальному MySQL из схемы панели. " +
                "Удалённый сервер автоматически не подставляется.");

        if (!Regex.IsMatch(result.Name, @"^[A-Za-z0-9_.-]{1,64}$"))
            throw new InvalidOperationException("Недопустимое имя базы данных.");

        if (!Regex.IsMatch(result.User, @"^[A-Za-z0-9_.-]{1,32}$"))
            throw new InvalidOperationException(
                "Недопустимое имя пользователя MySQL: разрешено до 32 символов.");

        if (result.Password.Length > 1024 ||
            result.Password.Any(char.IsControl))
            throw new InvalidOperationException(
                "Пароль БД содержит неподдерживаемые управляющие символы.");

        return result;
    }

    private static string Decode(string token)
    {
        if (token.Length < 2 ||
            (token[0] != '\'' && token[0] != '"') ||
            token[^1] != token[0])
            throw new InvalidOperationException(
                "В db ожидался строковый литерал PHP.");

        var quote = token[0];
        var result = new StringBuilder();

        for (var i = 1; i < token.Length - 1; i++)
        {
            var c = token[i];

            if (quote == '"' && c == '$')
                throw new InvalidOperationException(
                    "Интерполяция переменных PHP в db не поддерживается.");

            if (c != '\\')
            {
                result.Append(c);
                continue;
            }

            if (++i >= token.Length - 1)
                throw new InvalidOperationException("Незавершённая escape-последовательность.");

            var next = token[i];

            if (quote == '\'')
            {
                if (next == '\\' || next == '\'')
                    result.Append(next);
                else
                    result.Append('\\').Append(next);
                continue;
            }

            switch (next)
            {
                case '\\': result.Append('\\'); break;
                case '"': result.Append('"'); break;
                case '$': result.Append('$'); break;
                case 'n': result.Append('\n'); break;
                case 'r': result.Append('\r'); break;
                case 't': result.Append('\t'); break;
                case 'v': result.Append('\v'); break;
                case 'f': result.Append('\f'); break;
                case 'e': result.Append((char)27); break;
                default:
                    if (next == 'x' || next == 'u' ||
                        (next >= '0' && next <= '7'))
                        throw new InvalidOperationException(
                            "Числовые escape-последовательности PHP в db не поддерживаются.");
                    result.Append('\\').Append(next);
                    break;
            }
        }

        return result.ToString();
    }

    internal static string ClientOptions(
        string host, string user, string password)
    {
        static string Quote(string value) =>
            "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                       .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

        return "[client]\n" +
               "host=" + Quote(host) + "\n" +
               "user=" + Quote(user) + "\n" +
               "password=" + Quote(password) + "\n" +
               "default-character-set=utf8mb4\n";
    }
}