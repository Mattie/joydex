using System.Text;

namespace Joydex.Secrets;

/// <summary>
/// Parses the intentionally small dotenv subset accepted by the first secrets provider.
/// </summary>
internal static class StrictDotEnvParser
{
    public static Dictionary<string, string> Parse(string contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var position = 0;
        var line = 1;
        while (position < contents.Length)
        {
            SkipHorizontalWhitespace(contents, ref position);
            if (AtLineEnd(contents, position))
            {
                ConsumeLineEnd(contents, ref position, ref line);
                continue;
            }
            if (contents[position] == '#')
            {
                SkipComment(contents, ref position);
                ConsumeLineEnd(contents, ref position, ref line);
                continue;
            }

            ConsumeOptionalExport(contents, ref position);
            var key = ParseKey(contents, ref position, line);
            SkipHorizontalWhitespace(contents, ref position);
            if (position >= contents.Length || contents[position] != '=')
            {
                throw Error(line, "Expected '=' after the key.");
            }
            position++;
            SkipHorizontalWhitespace(contents, ref position);
            var value = ParseValue(contents, ref position, ref line);
            SkipHorizontalWhitespace(contents, ref position);
            if (position < contents.Length && contents[position] == '#')
            {
                SkipComment(contents, ref position);
            }
            if (!AtLineEnd(contents, position))
            {
                throw Error(line, "Unexpected characters after the value.");
            }
            ConsumeLineEnd(contents, ref position, ref line);
            if (!result.TryAdd(key, value))
            {
                throw Error(line - 1, $"The dotenv source repeats key '{key}'.");
            }
        }
        return result;
    }

    private static string ParseKey(string contents, ref int position, int line)
    {
        var start = position;
        while (position < contents.Length)
        {
            var character = contents[position];
            if (!(char.IsAsciiLetterOrDigit(character) || character == '_')) break;
            position++;
        }
        if (position == start || char.IsAsciiDigit(contents[start]))
        {
            throw Error(line, "A dotenv key must begin with a letter or underscore.");
        }
        return contents[start..position];
    }

    private static string ParseValue(string contents, ref int position, ref int line)
    {
        if (position >= contents.Length || AtLineEnd(contents, position)) return string.Empty;
        return contents[position] switch
        {
            '\'' => ParseQuoted(contents, ref position, ref line, '\''),
            '"' => ParseQuoted(contents, ref position, ref line, '"'),
            _ => ParseUnquoted(contents, ref position, line),
        };
    }

    private static string ParseQuoted(string contents, ref int position, ref int line, char quote)
    {
        position++;
        var builder = new StringBuilder();
        while (position < contents.Length)
        {
            var character = contents[position++];
            if (character == quote) return builder.ToString();
            if (character == '$' && quote == '"')
            {
                throw Error(line, "Dotenv interpolation is disabled.");
            }
            if (character == '\\' && quote == '"')
            {
                if (position >= contents.Length) throw Error(line, "An escape sequence is incomplete.");
                builder.Append(contents[position++] switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '\\' => '\\',
                    '"' => '"',
                    _ => throw Error(line, "The double-quoted value contains an unsupported escape."),
                });
                continue;
            }
            if (character == '\r')
            {
                if (position < contents.Length && contents[position] == '\n') position++;
                builder.Append('\n');
                line++;
                continue;
            }
            if (character == '\n')
            {
                builder.Append('\n');
                line++;
                continue;
            }
            builder.Append(character);
        }
        throw Error(line, "A quoted dotenv value is not terminated.");
    }

    private static string ParseUnquoted(string contents, ref int position, int line)
    {
        var start = position;
        var end = position;
        while (position < contents.Length && !AtLineEnd(contents, position))
        {
            if (contents[position] == '$') throw Error(line, "Dotenv interpolation is disabled.");
            if (contents[position] == '#'
                && (position == start || char.IsWhiteSpace(contents[position - 1])))
            {
                break;
            }
            position++;
            if (!char.IsWhiteSpace(contents[position - 1])) end = position;
        }
        return contents[start..end];
    }

    private static void ConsumeOptionalExport(string contents, ref int position)
    {
        const string prefix = "export";
        if (contents.AsSpan(position).StartsWith(prefix, StringComparison.Ordinal)
            && position + prefix.Length < contents.Length
            && char.IsWhiteSpace(contents[position + prefix.Length])
            && !AtLineEnd(contents, position + prefix.Length))
        {
            position += prefix.Length;
            SkipHorizontalWhitespace(contents, ref position);
        }
    }

    private static void SkipHorizontalWhitespace(string contents, ref int position)
    {
        while (position < contents.Length && contents[position] is ' ' or '\t') position++;
    }

    private static void SkipComment(string contents, ref int position)
    {
        while (position < contents.Length && !AtLineEnd(contents, position)) position++;
    }

    private static void ConsumeLineEnd(string contents, ref int position, ref int line)
    {
        if (position >= contents.Length) return;
        if (contents[position] == '\r')
        {
            position++;
            if (position < contents.Length && contents[position] == '\n') position++;
        }
        else if (contents[position] == '\n')
        {
            position++;
        }
        line++;
    }

    private static bool AtLineEnd(string contents, int position) =>
        position >= contents.Length || contents[position] is '\r' or '\n';

    private static InvalidDataException Error(int line, string message) =>
        new($"Dotenv line {line}: {message}");
}
