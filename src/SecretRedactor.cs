using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// ログへ credential / token を残さない。専用 PII SDK は使わず、既知の token 接頭辞と秘密フィールド名だけを対象にする。
/// </summary>
internal static class SecretRedactor
{
    public const string Replacement = "[REDACTED]";

    private static readonly HashSet<string> SecretPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "token",
        "access_token",
        "accessToken",
        "refresh_token",
        "refreshToken",
        "api_key",
        "apiKey",
        "apikey",
        "secret",
        "password",
        "credential",
        "credentials",
        "authorization",
        "auth",
        "xai_api_key",
        "github_token",
        "gh_token",
        "openai_api_key",
    };

    private static readonly Regex[] Patterns =
    [
        new(@"\bxai-[A-Za-z0-9_\-]{8,}", RegexOptions.Compiled),
        new(@"\bghp_[A-Za-z0-9]{20,}", RegexOptions.Compiled),
        new(@"\bgithub_pat_[A-Za-z0-9_]{20,}", RegexOptions.Compiled),
        new(@"\bsk-[A-Za-z0-9]{10,}", RegexOptions.Compiled),
        new(@"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", RegexOptions.Compiled),
        new(@"\bBearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"Authorization:\s*Basic\s+\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(
            @"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----",
            RegexOptions.Compiled),
        new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.Compiled),
        new(@"-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.Compiled),
        new(@"\bMII[A-Za-z0-9+/=]{16,}", RegexOptions.Compiled),
        new(
            @"\b(?:XAI_API_KEY|GH_TOKEN|GITHUB_TOKEN|OPENAI_API_KEY|ANTHROPIC_API_KEY|AWS_SECRET_ACCESS_KEY|AWS_ACCESS_KEY_ID)\s*[=:]\s*\S+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(
            @"(?:api[_-]?key|access[_-]?token|\btoken\b|secret|password|credential)\s*[=:]\s*\S+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    public static string RedactText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var current = text;
        foreach (var pattern in Patterns)
        {
            current = pattern.Replace(current, Replacement);
        }

        return current;
    }

    public static JsonElement Redact(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            Write(writer, element, propertyName: null);
        }

        return JsonSerializer.Deserialize<JsonElement>(stream.ToArray());
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element, string? propertyName)
    {
        if (propertyName is not null && SecretPropertyNames.Contains(propertyName))
        {
            writer.WriteStringValue(Replacement);
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value, property.Name);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    Write(writer, item, propertyName);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(RedactText(element.GetString() ?? string.Empty));
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}

/// <summary>
/// 人間向け .log 用に、巨大 payload を展開せず tool / mode の要点だけを拾う。
/// </summary>
internal static class AgentLogSummary
{
    private static readonly string[] FieldNames =
    [
        "toolName", "name", "title", "kind", "status", "command", "path", "file", "query",
        "mode", "currentMode", "sessionUpdate", "toolCallId",
    ];

    public static string DescribeTool(JsonElement root, bool started)
    {
        var source = UnwrapData(root);
        var name = First(source, root, "toolName", "name", "title") ?? "tool";
        var kind = First(source, root, "kind") ?? InferKind(name);
        var status = First(source, root, "status");
        var target = First(source, root, "path", "file", "command", "query")
            ?? CompactInput(source, root);
        var prefix = started ? "tool start " : "tool ";
        var builder = new StringBuilder(prefix);
        if (!started && !string.IsNullOrEmpty(status))
        {
            builder.Append(status);
        }

        var id = First(source, root, "toolCallId");
        if (!started && !string.IsNullOrEmpty(id))
        {
            if (builder[^1] != ' ')
            {
                builder.Append(' ');
            }

            builder.Append(id);
        }

        if (started || !string.Equals(name, "tool", StringComparison.Ordinal))
        {
            if (builder[^1] != ' ')
            {
                builder.Append(' ');
            }

            builder.Append(name);
        }

        if (!string.IsNullOrEmpty(kind))
        {
            builder.Append(" (");
            builder.Append(kind);
            builder.Append(')');
        }

        if (!string.IsNullOrEmpty(target))
        {
            builder.Append(' ');
            builder.Append(target);
        }

        return builder.ToString();
    }

    public static string DescribeModeOrLifecycle(string type, JsonElement root)
    {
        var source = UnwrapData(root);
        var mode = First(source, root, "mode", "currentMode", "sessionUpdate", "status");
        var extras = DescribeFields(root);
        if (string.IsNullOrEmpty(extras))
        {
            return string.IsNullOrEmpty(mode) ? type : type + " " + mode;
        }

        return type + " " + extras;
    }

    public static string DescribeFields(JsonElement root)
    {
        var source = UnwrapData(root);
        var parts = new List<string>();
        foreach (var name in FieldNames)
        {
            var value = CliJson.FindFirstString(source, name) ?? CliJson.FindFirstString(root, name);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            parts.Add(name + "=" + value);
        }

        return string.Join(" ", parts);
    }

    public static string DescribeGeneric(string type, JsonElement root)
    {
        var fields = DescribeFields(root);
        return string.IsNullOrEmpty(fields) ? type : type + " " + fields;
    }

    public static bool LooksLikeTool(string type, JsonElement root)
    {
        if (type.Contains("tool", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var source = UnwrapData(root);
        return CliJson.TryGetPropertyIgnoreCase(source, "toolName", out _)
            || CliJson.TryGetPropertyIgnoreCase(root, "toolName", out _)
            || CliJson.TryGetPropertyIgnoreCase(source, "rawInput", out _)
            || CliJson.TryGetPropertyIgnoreCase(root, "rawInput", out _);
    }

    public static bool LooksLikeMode(string type)
    {
        return type.Contains("mode", StringComparison.OrdinalIgnoreCase)
            || type.Contains("lifecycle", StringComparison.OrdinalIgnoreCase)
            || type.Contains("plan", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement UnwrapData(JsonElement root)
    {
        return CliJson.TryGetPropertyIgnoreCase(root, "data", out var data)
            && data.ValueKind == JsonValueKind.Object
            ? data
            : root;
    }

    private static string? First(JsonElement primary, JsonElement fallback, params string[] names)
    {
        return CliJson.FindFirstString(primary, names) ?? CliJson.FindFirstString(fallback, names);
    }

    private static string InferKind(string name)
    {
        if (name.Contains("read", StringComparison.OrdinalIgnoreCase))
        {
            return "read";
        }

        if (name.Contains("search", StringComparison.OrdinalIgnoreCase)
            || name.Contains("grep", StringComparison.OrdinalIgnoreCase))
        {
            return "search";
        }

        if (name.Contains("edit", StringComparison.OrdinalIgnoreCase)
            || name.Contains("write", StringComparison.OrdinalIgnoreCase)
            || name.Contains("search_replace", StringComparison.OrdinalIgnoreCase))
        {
            return "edit";
        }

        if (name.Contains("bash", StringComparison.OrdinalIgnoreCase)
            || name.Contains("shell", StringComparison.OrdinalIgnoreCase)
            || name.Contains("terminal", StringComparison.OrdinalIgnoreCase)
            || name.Contains("execute", StringComparison.OrdinalIgnoreCase)
            || name.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            return "execute";
        }

        return string.Empty;
    }

    private static string CompactInput(JsonElement source, JsonElement fallback)
    {
        if (CliJson.TryGetPropertyIgnoreCase(source, "rawInput", out var raw)
            || CliJson.TryGetPropertyIgnoreCase(fallback, "rawInput", out raw)
            || CliJson.TryGetPropertyIgnoreCase(source, "input", out raw)
            || CliJson.TryGetPropertyIgnoreCase(fallback, "input", out raw)
            || CliJson.TryGetPropertyIgnoreCase(source, "arguments", out raw)
            || CliJson.TryGetPropertyIgnoreCase(fallback, "arguments", out raw))
        {
            var path = CliJson.FindFirstString(raw, "path", "file", "command", "query");
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            if (raw.ValueKind == JsonValueKind.String)
            {
                return raw.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }
}
