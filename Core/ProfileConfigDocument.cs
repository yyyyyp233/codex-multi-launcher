using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexChannelLauncher.Core;

internal sealed record McpConfigInfo(
    string Name,
    string Transport,
    string Address,
    IReadOnlyList<string> Arguments,
    bool Enabled,
    bool ContainsSensitiveValues,
    string Fingerprint);

internal sealed partial class ProfileConfigDocument
{
    private readonly List<string> lines;
    private readonly string newline;

    private ProfileConfigDocument(string text)
    {
        newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .ToList();
    }

    public static ProfileConfigDocument Create() => new(string.Empty);

    public static ProfileConfigDocument Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Codex config.toml 不存在。", path);
        }

        return new ProfileConfigDocument(File.ReadAllText(path));
    }

    public string? GetRawValue(string? sectionName, string key)
    {
        var range = GetRange(sectionName);
        for (var index = range.Start; index <= range.End && index < lines.Count; index++)
        {
            if (TryReadKey(lines[index], out var candidate, out var value) &&
                candidate.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return StripInlineComment(value).Trim();
            }
        }

        return null;
    }

    public bool GetBool(string? sectionName, string key, bool fallback = false)
    {
        var raw = GetRawValue(sectionName, key);
        return bool.TryParse(raw, out var value) ? value : fallback;
    }

    public string GetString(string? sectionName, string key, string fallback = "")
    {
        var raw = GetRawValue(sectionName, key);
        return raw is null ? fallback : ParseTomlString(raw) ?? fallback;
    }

    public IReadOnlyList<string> GetStringArray(string? sectionName, string key) =>
        ParseTomlStringArray(GetRawValue(sectionName, key));

    public void SetRawValue(string? sectionName, string key, string rawValue)
    {
        var range = GetRange(sectionName);
        for (var index = range.Start; index <= range.End && index < lines.Count; index++)
        {
            if (TryReadKey(lines[index], out var candidate, out _) &&
                candidate.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                var indent = lines[index][..(lines[index].Length - lines[index].TrimStart().Length)];
                lines[index] = $"{indent}{key} = {rawValue}";
                return;
            }
        }

        if (sectionName is null)
        {
            var firstSection = FindSections().FirstOrDefault();
            var insertAt = firstSection is null ? lines.Count : firstSection.Start;
            if (insertAt > 0 && !string.IsNullOrWhiteSpace(lines[insertAt - 1]))
            {
                lines.Insert(insertAt++, string.Empty);
            }

            lines.Insert(insertAt, $"{key} = {rawValue}");
            return;
        }

        var section = FindSections()
            .FirstOrDefault(candidate => !candidate.IsArray &&
                                         candidate.Name.Equals(sectionName, StringComparison.OrdinalIgnoreCase));
        if (section is null)
        {
            AppendBlock([$"[{sectionName}]", $"{key} = {rawValue}"]);
            return;
        }

        var destination = section.End + 1;
        while (destination > section.Start + 1 && string.IsNullOrWhiteSpace(lines[destination - 1]))
        {
            destination--;
        }

        lines.Insert(destination, $"{key} = {rawValue}");
    }

    public void SetString(string? sectionName, string key, string value) =>
        SetRawValue(sectionName, key, Quote(value));

    public void SetBool(string? sectionName, string key, bool value) =>
        SetRawValue(sectionName, key, value ? "true" : "false");

    public void SetStringArray(string? sectionName, string key, IEnumerable<string> values) =>
        SetRawValue(sectionName, key, "[" + string.Join(", ", values.Select(Quote)) + "]");

    public void RemoveKey(string? sectionName, string key)
    {
        var range = GetRange(sectionName);
        for (var index = range.End; index >= range.Start && index < lines.Count; index--)
        {
            if (TryReadKey(lines[index], out var candidate, out _) &&
                candidate.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                lines.RemoveAt(index);
            }
        }
    }

    public void RemoveSectionTree(string sectionName)
    {
        var ranges = FindSections()
            .Where(section =>
                section.Name.Equals(sectionName, StringComparison.OrdinalIgnoreCase) ||
                section.Name.StartsWith(sectionName + ".", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(section => section.Start)
            .ToArray();
        foreach (var section in ranges)
        {
            lines.RemoveRange(section.Start, section.End - section.Start + 1);
        }

        CollapseBlankLines();
    }

    public bool HasNamedPermissionProfiles() =>
        GetRawValue(null, "default_permissions") is not null ||
        FindSections().Any(section =>
            section.Name.Equals("permissions", StringComparison.OrdinalIgnoreCase) ||
            section.Name.StartsWith("permissions.", StringComparison.OrdinalIgnoreCase));

    public bool IsPluginEnabled(string pluginId, bool fallback = false) =>
        GetBool($"plugins.\"{EscapeQuotedKey(pluginId)}\"", "enabled", fallback);

    public void SetPluginEnabled(string pluginId, bool enabled) =>
        SetBool($"plugins.\"{EscapeQuotedKey(pluginId)}\"", "enabled", enabled);

    public bool IsSkillEnabled(string skillPath)
    {
        foreach (var section in FindSections().Where(section => section.IsArray &&
                                                               section.Name.Equals("skills.config", StringComparison.OrdinalIgnoreCase)))
        {
            var configuredPath = GetValueWithin(section, "path");
            if (configuredPath is null || !PathsEqual(ParseTomlString(configuredPath), skillPath))
            {
                continue;
            }

            return !bool.TryParse(GetValueWithin(section, "enabled"), out var enabled) || enabled;
        }

        return true;
    }

    public void SetSkillEnabled(string skillPath, bool enabled)
    {
        foreach (var section in FindSections().Where(section => section.IsArray &&
                                                               section.Name.Equals("skills.config", StringComparison.OrdinalIgnoreCase)))
        {
            var configuredPath = GetValueWithin(section, "path");
            if (configuredPath is null || !PathsEqual(ParseTomlString(configuredPath), skillPath))
            {
                continue;
            }

            SetValueWithin(section, "enabled", enabled ? "true" : "false");
            return;
        }

        if (!enabled)
        {
            AppendBlock(["[[skills.config]]", $"path = {Quote(skillPath)}", "enabled = false"]);
        }
    }

    public IReadOnlyList<McpConfigInfo> GetMcpServers()
    {
        var sections = FindSections();
        var names = sections
            .Select(section => SplitDottedName(section.Name))
            .Where(parts => parts.Count >= 2 && parts[0].Equals("mcp_servers", StringComparison.OrdinalIgnoreCase))
            .Select(parts => parts[1])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return names.Select(name => ReadMcpServer(name, sections)).ToArray();
    }

    public void SetMcpEnabled(string name, bool enabled)
    {
        var rootSection = FindMcpRootSection(name)
                          ?? throw new InvalidOperationException($"MCP 不存在：{name}");
        SetValueWithin(rootSection, "enabled", enabled ? "true" : "false");
    }

    public void RemoveMcpServer(string name)
    {
        var ranges = FindSections()
            .Where(section => IsMcpSectionFor(section.Name, name))
            .OrderByDescending(section => section.Start)
            .ToArray();
        foreach (var section in ranges)
        {
            lines.RemoveRange(section.Start, section.End - section.Start + 1);
        }

        CollapseBlankLines();
    }

    public void ImportMcpServer(ProfileConfigDocument source, string name)
    {
        var info = source.GetMcpServers().FirstOrDefault(server =>
            server.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException($"个人配置中不存在 MCP：{name}");
        if (info.ContainsSensitiveValues)
        {
            throw new InvalidOperationException("该 MCP 包含静态 env、Header 或疑似密钥值。为防止个人凭据进入工作空间，启动器拒绝自动迁移。");
        }

        var sourceSections = source.FindSections()
            .Where(section => IsMcpSectionFor(section.Name, name))
            .OrderBy(section => section.Start)
            .ToArray();
        var block = new List<string>();
        foreach (var section in sourceSections)
        {
            if (block.Count > 0)
            {
                block.Add(string.Empty);
            }

            block.AddRange(source.lines.Skip(section.Start).Take(section.End - section.Start + 1));
        }

        RemoveMcpServer(name);
        AppendBlock(block);
    }

    public void UpsertMcpServer(
        string name,
        string transport,
        string address,
        IEnumerable<string> arguments,
        bool enabled)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(character => char.IsControl(character)))
        {
            throw new ArgumentException("MCP 名称不能为空或包含控制字符。", nameof(name));
        }

        var rootSection = FindMcpRootSection(name);
        var sectionName = rootSection?.Name ?? $"mcp_servers.\"{EscapeQuotedKey(name.Trim())}\"";
        if (transport.Equals("HTTP", StringComparison.OrdinalIgnoreCase))
        {
            SetString(sectionName, "url", address.Trim());
            RemoveKey(sectionName, "command");
            RemoveKey(sectionName, "args");
        }
        else
        {
            SetString(sectionName, "command", address.Trim());
            RemoveKey(sectionName, "url");
            SetStringArray(sectionName, "args", arguments.Where(argument => !string.IsNullOrWhiteSpace(argument)));
        }

        SetBool(sectionName, "enabled", enabled);
    }

    public void SaveAtomic(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(string.Join(newline, lines));
            writer.Flush();
            stream.Flush(true);
        }

        File.Move(temporary, path, true);
    }

    private McpConfigInfo ReadMcpServer(string name, IReadOnlyList<SectionSpan> sections)
    {
        var root = sections.FirstOrDefault(section =>
            !section.IsArray && SplitDottedName(section.Name) is var parts &&
            parts.Count == 2 && parts[0].Equals("mcp_servers", StringComparison.OrdinalIgnoreCase) &&
            parts[1].Equals(name, StringComparison.OrdinalIgnoreCase));
        var command = root is null ? null : ParseTomlString(GetValueWithin(root, "command"));
        var url = root is null ? null : ParseTomlString(GetValueWithin(root, "url"));
        var enabledRaw = root is null ? null : GetValueWithin(root, "enabled");
        var enabled = !bool.TryParse(enabledRaw, out var parsedEnabled) || parsedEnabled;
        var args = root is null
            ? []
            : ParseTomlStringArray(GetValueWithin(root, "args"));
        var related = sections.Where(section => IsMcpSectionFor(section.Name, name)).ToArray();
        var normalized = string.Join("\n", related.SelectMany(section =>
                lines.Skip(section.Start).Take(section.End - section.Start + 1))
            .Select(line => StripInlineComment(line).Trim())
            .Where(line => line.Length > 0));
        var sensitive = related.Any(SectionContainsSensitiveValues);
        return new McpConfigInfo(
            name,
            url is not null ? "HTTP" : "STDIO",
            url ?? command ?? string.Empty,
            args,
            enabled,
            sensitive,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))));
    }

    private bool SectionContainsSensitiveValues(SectionSpan section)
    {
        var parts = SplitDottedName(section.Name);
        if (parts.Count >= 3 && parts[^1].Equals("env", StringComparison.OrdinalIgnoreCase))
        {
            return Enumerable.Range(section.Start + 1, Math.Max(0, section.End - section.Start))
                .Any(index => index < lines.Count && TryReadKey(lines[index], out _, out _));
        }

        for (var index = section.Start + 1; index <= section.End && index < lines.Count; index++)
        {
            if (!TryReadKey(lines[index], out var key, out _))
            {
                continue;
            }

            var lower = key.ToLowerInvariant();
            if (lower is "env_vars" or "env_http_headers" or "bearer_token_env_var")
            {
                continue;
            }

            if (lower is "env" or "http_headers" ||
                lower.Contains("token", StringComparison.Ordinal) ||
                lower.Contains("secret", StringComparison.Ordinal) ||
                lower.Contains("password", StringComparison.Ordinal) ||
                lower.Contains("api_key", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private SectionSpan? FindMcpRootSection(string name) =>
        FindSections().FirstOrDefault(section =>
            !section.IsArray && SplitDottedName(section.Name) is var parts &&
            parts.Count == 2 && parts[0].Equals("mcp_servers", StringComparison.OrdinalIgnoreCase) &&
            parts[1].Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool IsMcpSectionFor(string sectionName, string serverName)
    {
        var parts = SplitDottedName(sectionName);
        return parts.Count >= 2 &&
               parts[0].Equals("mcp_servers", StringComparison.OrdinalIgnoreCase) &&
               parts[1].Equals(serverName, StringComparison.OrdinalIgnoreCase);
    }

    private (int Start, int End) GetRange(string? sectionName)
    {
        var sections = FindSections();
        if (sectionName is null)
        {
            return (0, sections.FirstOrDefault()?.Start - 1 ?? lines.Count - 1);
        }

        var section = sections.FirstOrDefault(candidate => !candidate.IsArray &&
                                                           candidate.Name.Equals(sectionName, StringComparison.OrdinalIgnoreCase));
        return section is null ? (0, -1) : (section.Start + 1, section.End);
    }

    private IReadOnlyList<SectionSpan> FindSections()
    {
        var result = new List<SectionSpan>();
        for (var index = 0; index < lines.Count; index++)
        {
            var trimmed = lines[index].Trim();
            var isArray = trimmed.StartsWith("[[", StringComparison.Ordinal) &&
                          trimmed.EndsWith("]]", StringComparison.Ordinal);
            var isTable = !isArray && trimmed.StartsWith("[", StringComparison.Ordinal) &&
                          trimmed.EndsWith("]", StringComparison.Ordinal);
            if (!isArray && !isTable)
            {
                continue;
            }

            var name = isArray ? trimmed[2..^2].Trim() : trimmed[1..^1].Trim();
            if (result.Count > 0)
            {
                result[^1] = result[^1] with { End = index - 1 };
            }

            result.Add(new SectionSpan(name, isArray, index, lines.Count - 1));
        }

        return result;
    }

    private string? GetValueWithin(SectionSpan section, string key)
    {
        for (var index = section.Start + 1; index <= section.End && index < lines.Count; index++)
        {
            if (TryReadKey(lines[index], out var candidate, out var value) &&
                candidate.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return StripInlineComment(value).Trim();
            }
        }

        return null;
    }

    private void SetValueWithin(SectionSpan section, string key, string rawValue)
    {
        for (var index = section.Start + 1; index <= section.End && index < lines.Count; index++)
        {
            if (TryReadKey(lines[index], out var candidate, out _) &&
                candidate.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                lines[index] = $"{key} = {rawValue}";
                return;
            }
        }

        lines.Insert(section.End + 1, $"{key} = {rawValue}");
    }

    private void AppendBlock(IEnumerable<string> block)
    {
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count > 0)
        {
            lines.Add(string.Empty);
        }

        lines.AddRange(block);
        lines.Add(string.Empty);
    }

    private void CollapseBlankLines()
    {
        for (var index = lines.Count - 1; index > 0; index--)
        {
            if (string.IsNullOrWhiteSpace(lines[index]) && string.IsNullOrWhiteSpace(lines[index - 1]))
            {
                lines.RemoveAt(index);
            }
        }
    }

    private static bool TryReadKey(string line, out string key, out string value)
    {
        var match = KeyRegex().Match(line);
        if (!match.Success)
        {
            key = string.Empty;
            value = string.Empty;
            return false;
        }

        key = match.Groups["key"].Value;
        value = match.Groups["value"].Value;
        return true;
    }

    private static string StripInlineComment(string value)
    {
        var quote = '\0';
        var escaped = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (quote == '"' && character == '\\')
            {
                escaped = true;
                continue;
            }

            if (character is '"' or '\'')
            {
                quote = quote == '\0' ? character : quote == character ? '\0' : quote;
                continue;
            }

            if (character == '#' && quote == '\0')
            {
                return value[..index];
            }
        }

        return value;
    }

    private static string? ParseTomlString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        raw = StripInlineComment(raw).Trim();
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            return raw[1..^1]
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
        {
            return raw[1..^1];
        }

        return raw;
    }

    private static IReadOnlyList<string> ParseTomlStringArray(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        raw = StripInlineComment(raw).Trim();
        if (!raw.StartsWith("[", StringComparison.Ordinal) || !raw.EndsWith("]", StringComparison.Ordinal))
        {
            return [];
        }

        var result = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        var escaped = false;
        foreach (var character in raw[1..^1])
        {
            if (escaped)
            {
                current.Append(character);
                escaped = false;
                continue;
            }

            if (quote == '"' && character == '\\')
            {
                current.Append(character);
                escaped = true;
                continue;
            }

            if (character is '"' or '\'')
            {
                current.Append(character);
                quote = quote == '\0' ? character : quote == character ? '\0' : quote;
                continue;
            }

            if (character == ',' && quote == '\0')
            {
                var parsed = ParseTomlString(current.ToString().Trim());
                if (parsed is not null)
                {
                    result.Add(parsed);
                }

                current.Clear();
                continue;
            }

            current.Append(character);
        }

        var final = ParseTomlString(current.ToString().Trim());
        if (final is not null)
        {
            result.Add(final);
        }

        return result;
    }

    private static IReadOnlyList<string> SplitDottedName(string value)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        var escaped = false;
        foreach (var character in value)
        {
            if (escaped)
            {
                current.Append(character);
                escaped = false;
                continue;
            }

            if (quote == '"' && character == '\\')
            {
                escaped = true;
                continue;
            }

            if (character is '"' or '\'')
            {
                quote = quote == '\0' ? character : quote == character ? '\0' : quote;
                continue;
            }

            if (character == '.' && quote == '\0')
            {
                result.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(character);
        }

        result.Add(current.ToString());
        return result;
    }

    private static bool PathsEqual(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(left).TrimEnd('\\', '/'),
                Path.GetFullPath(right).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string EscapeQuotedKey(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private sealed record SectionSpan(string Name, bool IsArray, int Start, int End);

    [GeneratedRegex(@"^\s*(?<key>[A-Za-z0-9_.-]+)\s*=\s*(?<value>.*)$")]
    private static partial Regex KeyRegex();
}
