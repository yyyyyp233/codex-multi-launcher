using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexChannelLauncher.Core;

public sealed record MergeBaseRecord(
    int SchemaVersion,
    string ResourceKey,
    bool Exists,
    string Content,
    DateTime UpdatedAtUtc);

public sealed class MergeBaseStore(string rootDirectory)
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public bool Has(MergeResourceKind kind, string containerName, string relativePath) =>
        File.Exists(GetRecordPath(BuildResourceKey(kind, containerName, relativePath)));

    public MergeBaseRecord? TryLoad(MergeResourceKind kind, string containerName, string relativePath)
    {
        var key = BuildResourceKey(kind, containerName, relativePath);
        var path = GetRecordPath(key);
        if (!File.Exists(path))
        {
            return null;
        }

        var record = JsonSerializer.Deserialize<MergeBaseRecord>(File.ReadAllText(path, Encoding.UTF8), JsonOptions)
                     ?? throw new InvalidDataException("共同基线记录不可解析。");
        if (record.SchemaVersion != CurrentSchemaVersion ||
            !record.ResourceKey.Equals(key, StringComparison.Ordinal))
        {
            throw new InvalidDataException("共同基线记录与当前文件不匹配。");
        }

        return record;
    }

    public void Save(
        MergeResourceKind kind,
        string containerName,
        string relativePath,
        bool exists,
        string normalizedContent)
    {
        Directory.CreateDirectory(rootDirectory);
        var key = BuildResourceKey(kind, containerName, relativePath);
        var path = GetRecordPath(key);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        var record = new MergeBaseRecord(
            CurrentSchemaVersion,
            key,
            exists,
            exists ? TextFileCodec.NormalizeNewLines(normalizedContent) : string.Empty,
            DateTime.UtcNow);
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(record, JsonOptions), new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch
            {
                // A stale temporary baseline is ignored because only the hashed .json path is authoritative.
            }
        }
    }

    internal static string BuildResourceKey(
        MergeResourceKind kind,
        string containerName,
        string relativePath)
    {
        var normalizedContainer = containerName.Trim().Replace('\\', '/').ToUpperInvariant();
        var normalizedRelative = relativePath.Trim().Replace('\\', '/').TrimStart('/').ToUpperInvariant();
        return $"{kind.ToString().ToLowerInvariant()}|{normalizedContainer}|{normalizedRelative}";
    }

    private string GetRecordPath(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(rootDirectory, hash + ".json");
    }
}
