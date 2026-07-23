using System.Security.Cryptography;
using System.Text.Json;

namespace CodexChannelLauncher.Core;

public sealed class CodexRuntimeCache(LauncherPaths paths, LauncherLog log)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string Prepare(
        CodexPackageInfo package,
        string profileId,
        string badgeColor,
        Action<LaunchProgress>? progress,
        CancellationToken cancellationToken)
    {
        paths.EnsureRuntimeDirectories();
        var sourceApp = Path.GetDirectoryName(package.ExecutablePath)
                        ?? throw new InvalidOperationException("无法解析 Codex App 安装目录。");
        var sourceEntryExecutable = package.ExecutablePath;
        if (!File.Exists(sourceEntryExecutable))
        {
            throw new FileNotFoundException("当前 Codex App 缺少入口程序 ChatGPT.exe。", sourceEntryExecutable);
        }

        var sourceHash = ComputeSha256(sourceEntryExecutable);
        var packageFingerprint = ComputeSha256(package.ExecutablePath);
        var cacheKey = $"{Sanitize(package.PackageVersion)}-{packageFingerprint[..12].ToLowerInvariant()}-" +
                       $"{Sanitize(profileId)}-{Sanitize(badgeColor.TrimStart('#').ToLowerInvariant())}";
        var versionsRoot = Path.Combine(paths.RuntimeCacheRoot, "versions");
        var finalRoot = Path.Combine(versionsRoot, cacheKey);
        var finalApp = Path.Combine(finalRoot, "app");
        var finalExecutable = Path.Combine(finalApp, "ChatGPT.exe");

        Directory.CreateDirectory(versionsRoot);
        using var mutex = new Mutex(false, $"Local\\CodexChannelLauncher.RuntimeCache.{cacheKey}");
        var lockTaken = false;
        try
        {
            try
            {
                lockTaken = mutex.WaitOne(TimeSpan.FromMinutes(12));
            }
            catch (AbandonedMutexException)
            {
                lockTaken = true;
            }

            if (!lockTaken)
            {
                throw new TimeoutException("等待 Codex 本机运行副本准备完成超时。");
            }

            if (Validate(finalRoot, sourceApp, package, sourceHash, profileId, badgeColor))
            {
                progress?.Invoke(new LaunchProgress("runtime-cache", 100, "已验证本机运行副本"));
                return finalExecutable;
            }

            if (Directory.Exists(finalRoot) &&
                TryAdoptCompleteClone(finalRoot, sourceApp, package, sourceHash, profileId, badgeColor) &&
                Validate(finalRoot, sourceApp, package, sourceHash, profileId, badgeColor))
            {
                progress?.Invoke(new LaunchProgress("runtime-cache", 100, "已升级并验证本机运行副本"));
                return finalExecutable;
            }

            if (Directory.Exists(finalRoot))
            {
                throw new IOException($"Codex 运行副本不完整，请手动移走后重试：{finalRoot}");
            }

            var stagingRoot = Path.Combine(versionsRoot, cacheKey + ".staging-" + Guid.NewGuid().ToString("N"));
            var stagingApp = Path.Combine(stagingRoot, "app");
            Directory.CreateDirectory(stagingApp);

            try
            {
                progress?.Invoke(new LaunchProgress("runtime-cache", 0, "首次准备独立运行副本（约 1.9 GB）"));
                var files = Directory.EnumerateFiles(sourceApp, "*", SearchOption.AllDirectories)
                    .Select(file => new FileInfo(file))
                    .ToArray();
                var totalBytes = files.Sum(file => file.Length);
                long copiedBytes = 0;
                long copiedFiles = 0;
                var lastReported = -1;

                Parallel.ForEach(
                    files,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = 4,
                        CancellationToken = cancellationToken
                    },
                    file =>
                    {
                        var relative = Path.GetRelativePath(sourceApp, file.FullName);
                        var destination = Path.Combine(stagingApp, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Copy(file.FullName, destination, false);
                        File.SetLastWriteTimeUtc(destination, file.LastWriteTimeUtc);

                        var bytesNow = Interlocked.Add(ref copiedBytes, file.Length);
                        Interlocked.Increment(ref copiedFiles);
                        var percent = totalBytes == 0
                            ? 100
                            : (int)Math.Clamp(bytesNow * 100 / totalBytes, 0, 99);
                        var observed = Volatile.Read(ref lastReported);
                        if (percent > observed && Interlocked.CompareExchange(ref lastReported, percent, observed) == observed)
                        {
                            progress?.Invoke(new LaunchProgress(
                                "runtime-cache",
                                percent,
                                $"正在准备独立运行副本 · {percent}%"));
                        }
                    });

                var targetExecutable = Path.Combine(stagingApp, "ChatGPT.exe");
                if (!File.Exists(targetExecutable) ||
                    !ComputeSha256(targetExecutable).Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("运行副本的 ChatGPT.exe 哈希校验失败。");
                }

                var copied = new DirectoryInfo(stagingApp)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .ToArray();
                var copiedTotalBytes = copied.Sum(file => file.Length);
                if (copiedFiles != files.LongLength || copiedTotalBytes != totalBytes)
                {
                    throw new IOException("运行副本文件数量或总大小校验失败。");
                }

                var branding = CompanyTrayIconBranding.Apply(sourceApp, stagingApp, badgeColor, log);

                var manifest = new RuntimeCacheManifest(
                    package.PackageVersion,
                    package.InstallLocation,
                    copiedFiles,
                    copiedTotalBytes,
                    "ChatGPT.exe",
                    sourceHash,
                    DateTime.UtcNow,
                    branding.Version,
                    branding.Applied,
                    branding.FileSha256,
                    profileId,
                    badgeColor.ToUpperInvariant());
                File.WriteAllText(
                    Path.Combine(stagingRoot, "cache-manifest.json"),
                    JsonSerializer.Serialize(manifest, JsonOptions));

                if (!Validate(stagingRoot, sourceApp, package, sourceHash, profileId, badgeColor))
                {
                    throw new IOException("运行副本的工作空间托盘标识或缓存清单校验失败。");
                }

                Directory.Move(stagingRoot, finalRoot);
                progress?.Invoke(new LaunchProgress("runtime-cache", 100, "独立运行副本已校验完成"));
                log.Info(
                    $"Runtime cache prepared: package={package.PackageVersion}, files={copiedFiles}, " +
                    $"bytes={copiedTotalBytes}, trayBranding={branding.Applied}");
                return finalExecutable;
            }
            catch
            {
                if (Directory.Exists(stagingRoot) && LauncherPaths.IsUnder(stagingRoot, paths.RuntimeCacheRoot))
                {
                    try
                    {
                        Directory.Delete(stagingRoot, true);
                    }
                    catch
                    {
                        // Keep a partial staging folder for manual diagnostics if cleanup is blocked.
                    }
                }

                throw;
            }
        }
        finally
        {
            if (lockTaken)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static bool Validate(
        string cacheRoot,
        string sourceApp,
        CodexPackageInfo package,
        string sourceHash,
        string profileId,
        string badgeColor)
    {
        try
        {
            var manifestPath = Path.Combine(cacheRoot, "cache-manifest.json");
            var executablePath = Path.Combine(cacheRoot, "app", "ChatGPT.exe");
            var asarPath = Path.Combine(cacheRoot, "app", "resources", "app.asar");
            var codexPath = Path.Combine(cacheRoot, "app", "resources", "codex.exe");
            if (!File.Exists(manifestPath) || !File.Exists(executablePath) ||
                !File.Exists(asarPath) || !File.Exists(codexPath))
            {
                return false;
            }

            var manifest = JsonSerializer.Deserialize<RuntimeCacheManifest>(File.ReadAllText(manifestPath), JsonOptions);
            if (manifest is null ||
                !string.Equals(manifest.PackageVersion, package.PackageVersion, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(manifest.EntryExecutableName, "ChatGPT.exe", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(manifest.EntryExecutableSha256, sourceHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(manifest.ProfileId, profileId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(manifest.BadgeColor, badgeColor, StringComparison.OrdinalIgnoreCase) ||
                !ComputeSha256(executablePath).Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var cachedApp = Path.Combine(cacheRoot, "app");
            return ContainsCompleteSourceTree(
                       sourceApp,
                       cachedApp,
                       true,
                       out var sourceFileCount,
                       out var sourceTotalBytes) &&
                   sourceFileCount == manifest.FileCount &&
                   sourceTotalBytes == manifest.TotalBytes &&
                   CompanyTrayIconBranding.Validate(sourceApp, cachedApp, manifest);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryAdoptCompleteClone(
        string cacheRoot,
        string sourceApp,
        CodexPackageInfo package,
        string sourceEntryHash,
        string profileId,
        string badgeColor)
    {
        try
        {
            var cachedApp = Path.Combine(cacheRoot, "app");
            var cachedEntry = Path.Combine(cachedApp, "ChatGPT.exe");
            if (!File.Exists(cachedEntry) ||
                !ComputeSha256(cachedEntry).Equals(sourceEntryHash, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!ContainsCompleteSourceTree(
                    sourceApp,
                    cachedApp,
                    true,
                    out var sourceFileCount,
                    out var sourceTotalBytes))
            {
                return false;
            }

            var branding = CompanyTrayIconBranding.Apply(sourceApp, cachedApp, badgeColor);

            var manifest = new RuntimeCacheManifest(
                package.PackageVersion,
                package.InstallLocation,
                sourceFileCount,
                sourceTotalBytes,
                "ChatGPT.exe",
                sourceEntryHash,
                DateTime.UtcNow,
                branding.Version,
                branding.Applied,
                branding.FileSha256,
                profileId,
                badgeColor.ToUpperInvariant());
            File.WriteAllText(
                Path.Combine(cacheRoot, "cache-manifest.json"),
                JsonSerializer.Serialize(manifest, JsonOptions));
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool ContainsCompleteSourceTree(
        string sourceApp,
        string cachedApp,
        bool allowManagedTrayIconOverrides,
        out long sourceFileCount,
        out long sourceTotalBytes)
    {
        sourceFileCount = 0;
        sourceTotalBytes = 0;

        if (!Directory.Exists(sourceApp) || !Directory.Exists(cachedApp))
        {
            return false;
        }

        var sourceFiles = Directory.EnumerateFiles(sourceApp, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => NormalizeRelativePath(sourceApp, path),
                path => new FileInfo(path),
                StringComparer.OrdinalIgnoreCase);
        var cachedFiles = Directory.EnumerateFiles(cachedApp, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => NormalizeRelativePath(cachedApp, path),
                path => new FileInfo(path),
                StringComparer.OrdinalIgnoreCase);
        if (sourceFiles.Count == 0 ||
            sourceFiles.Count != cachedFiles.Count ||
            sourceFiles.Keys.Any(relativePath => !cachedFiles.ContainsKey(relativePath)))
        {
            return false;
        }

        foreach (var (relativePath, sourceFile) in sourceFiles)
        {
            var cachedFile = cachedFiles[relativePath];
            var managedOverride = allowManagedTrayIconOverrides &&
                                  CompanyTrayIconBranding.IsManagedRelativePath(relativePath);
            if (!managedOverride &&
                (cachedFile.Length != sourceFile.Length ||
                 !ComputeSha256(cachedFile.FullName).Equals(
                     ComputeSha256(sourceFile.FullName),
                     StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            sourceFileCount++;
            sourceTotalBytes += sourceFile.Length;
        }

        return sourceFileCount > 0;
    }

    private static string NormalizeRelativePath(string root, string filePath) =>
        Path.GetRelativePath(root, filePath).Replace(
            Path.AltDirectorySeparatorChar,
            Path.DirectorySeparatorChar);

    private static string ComputeSha256(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 128 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string Sanitize(string value) =>
        string.Concat(value.Select(character => char.IsLetterOrDigit(character) || character is '.' or '-'
            ? character
            : '_'));
}
