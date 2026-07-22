namespace CodexChannelLauncher.Core;

public static class ProfileContentPolicy
{
    private static readonly IReadOnlyList<string> GlobalRuleNames =
        Array.AsReadOnly(["AGENTS.override.md", "AGENTS.md"]);

    public static IReadOnlyList<string> GlobalRuleFileNames => GlobalRuleNames;

    public static bool IsGlobalRulePath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').Trim('/');
        return !normalized.Contains('/') &&
               GlobalRuleNames.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsManagedMemoryPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        var first = segments[0];
        if (first.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
            first.Equals(".agents", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("_rebuild_mem", StringComparison.OrdinalIgnoreCase) ||
            first.StartsWith("tmp", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("stdout", StringComparison.OrdinalIgnoreCase) ||
            segments.Any(segment => segment.Equals(".git", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var fileName = segments[^1];
        return !fileName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) &&
               !fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
               !fileName.EndsWith(".wal", StringComparison.OrdinalIgnoreCase) &&
               !fileName.EndsWith(".shm", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsGeneratedMemoryAggregate(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        return normalized.Equals("MEMORY.md", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("memory_summary.md", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("raw_memories.md", StringComparison.OrdinalIgnoreCase);
    }
}
