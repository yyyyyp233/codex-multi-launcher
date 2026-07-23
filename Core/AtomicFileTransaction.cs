namespace CodexChannelLauncher.Core;

internal sealed record AtomicFileChange(string TargetPath, string? ReplacementPath);

internal static class AtomicFileTransaction
{
    public static void Commit(
        LauncherPaths paths,
        IReadOnlyList<AtomicFileChange> changes)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            return;
        }

        Validate(paths, changes);
        var transactionRoot = Path.Combine(
            paths.OperationStagingRoot,
            "file-transaction-" + Guid.NewGuid().ToString("N"));
        var backupRoot = Path.Combine(transactionRoot, "backups");
        Directory.CreateDirectory(backupRoot);

        var applied = new List<AppliedChange>();
        try
        {
            for (var index = 0; index < changes.Count; index++)
            {
                var change = changes[index];
                var target = Path.GetFullPath(change.TargetPath);
                var backup = Path.Combine(backupRoot, $"{index:D3}.bak");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                LauncherPaths.EnsureNoReparsePoints(Path.GetDirectoryName(target)!);

                var hadOriginal = File.Exists(target);
                if (hadOriginal)
                {
                    File.Move(target, backup);
                }

                var current = new AppliedChange(target, backup, hadOriginal);
                applied.Add(current);
                if (change.ReplacementPath is not null)
                {
                    File.Move(Path.GetFullPath(change.ReplacementPath), target);
                }
            }
        }
        catch (Exception operationException)
        {
            try
            {
                RollBack(applied);
                ProfileSnapshotService.SafeDeleteDirectory(transactionRoot);
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    $"文件事务失败且回滚不完整；保留目录：{transactionRoot}",
                    operationException,
                    rollbackException);
            }

            throw;
        }

        ProfileSnapshotService.SafeDeleteDirectory(transactionRoot);
    }

    private static void Validate(
        LauncherPaths paths,
        IReadOnlyList<AtomicFileChange> changes)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            var target = Path.GetFullPath(change.TargetPath);
            if (!LauncherPaths.IsUnder(target, paths.RuntimeRoot) ||
                !targets.Add(target) ||
                Directory.Exists(target))
            {
                throw new InvalidOperationException("文件事务包含越界、重复或目录目标。");
            }

            LauncherPaths.EnsureNoReparsePoints(target);
            if (change.ReplacementPath is null)
            {
                continue;
            }

            var replacement = Path.GetFullPath(change.ReplacementPath);
            if (!LauncherPaths.IsUnder(replacement, paths.OperationStagingRoot) ||
                !File.Exists(replacement))
            {
                throw new InvalidOperationException("文件事务替换源不在受控暂存目录中。");
            }

            LauncherPaths.EnsureNoReparsePoints(replacement);
        }
    }

    private static void RollBack(IReadOnlyList<AppliedChange> applied)
    {
        for (var index = applied.Count - 1; index >= 0; index--)
        {
            var change = applied[index];
            if (File.Exists(change.TargetPath))
            {
                File.Delete(change.TargetPath);
            }

            if (change.HadOriginal)
            {
                if (!File.Exists(change.BackupPath))
                {
                    throw new IOException($"文件事务备份缺失：{change.BackupPath}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(change.TargetPath)!);
                File.Move(change.BackupPath, change.TargetPath);
            }
        }
    }

    private sealed record AppliedChange(
        string TargetPath,
        string BackupPath,
        bool HadOriginal);
}
