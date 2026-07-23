using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexChannelLauncher.Core;

public sealed record SnapshotFileRecord(string Path, long Length, string Sha256);

public sealed record ProfileSnapshotManifest(
    string Id,
    DateTime CreatedAtUtc,
    string Reason,
    bool HasConfig,
    bool HasAgents,
    IReadOnlyList<SnapshotFileRecord> Files,
    string Target = "company",
    bool? GlobalRulesCaptured = null);

public sealed record SnapshotSummary(
    string Id,
    DateTime CreatedAtUtc,
    string Reason,
    string ArchivePath,
    int FileCount,
    long TotalBytes,
    string Target = "company")
{
    public string DisplayName =>
        $"{CreatedAtUtc.ToLocalTime():MM-dd HH:mm:ss} · {(Target == "personal" ? "个人" : "工作空间")} · {Reason}";
}

public sealed class ProfileSnapshotService(LauncherPaths paths)
{
    private const int RetentionCount = 10;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public SnapshotSummary CreateSnapshot(string reason) => CreateSnapshotCore(reason, "company");

    public SnapshotSummary CreatePersonalSnapshot(string reason) => CreateSnapshotCore(reason, "personal");

    private SnapshotSummary CreateSnapshotCore(
        string reason,
        string target,
        string? protectedArchivePath = null)
    {
        paths.EnsureRuntimeDirectories();
        target = NormalizeTarget(target);
        var now = DateTime.UtcNow;
        var idStem = $"{now:yyyyMMdd-HHmmss}-{target}-{Sanitize(reason)}-{Guid.NewGuid():N}";
        var id = idStem[..Math.Min(64, idStem.Length)];
        var finalPath = Path.Combine(paths.SnapshotDirectory, id + ".zip");
        var temporaryPath = finalPath + ".tmp";
        var records = new List<SnapshotFileRecord>();
        var companyTarget = target == "company";
        var configPath = companyTarget ? paths.CompanyConfig : string.Empty;
        var targetHome = companyTarget ? paths.CompanyCodexHome : paths.PersonalCodexHome;
        var agentsPath = Path.Combine(targetHome, "AGENTS.md");
        var agentsOverridePath = Path.Combine(targetHome, "AGENTS.override.md");
        var skillsPath = companyTarget ? paths.CompanySkills : paths.PersonalSkills;
        var memoriesPath = companyTarget ? paths.CompanyMemories : paths.PersonalMemories;

        try
        {
            using (var file = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.ReadWrite,
                       FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
            {
                AddFile(archive, configPath, "profile/config.toml", records);
                AddFile(archive, agentsPath, "profile/AGENTS.md", records);
                AddFile(archive, agentsOverridePath, "profile/AGENTS.override.md", records);
                AddDirectory(archive, skillsPath, "profile/skills", records,
                    relative => !IsSystemSkill(relative));
                AddDirectory(archive, memoriesPath, "profile/memories", records, _ => true);

                var manifest = new ProfileSnapshotManifest(
                    id,
                    now,
                    reason,
                    companyTarget && File.Exists(configPath),
                    File.Exists(agentsPath),
                    records,
                    target,
                    true);
                var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                using var writer = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false));
                writer.Write(JsonSerializer.Serialize(manifest, JsonOptions));
            }

            File.Move(temporaryPath, finalPath, false);
        }
        catch
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // Preserve the snapshot creation exception; a stale .tmp is never restorable.
            }

            throw;
        }

        PruneOldSnapshots(protectedArchivePath);
        return ToSummary(finalPath, ReadManifest(finalPath));
    }

    public IReadOnlyList<SnapshotSummary> ListSnapshots()
    {
        paths.EnsureRuntimeDirectories();
        var result = new List<SnapshotSummary>();
        foreach (var archivePath in Directory.EnumerateFiles(paths.SnapshotDirectory, "*.zip")
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                result.Add(ToSummary(archivePath, ReadManifest(archivePath)));
            }
            catch
            {
                // A damaged archive stays on disk for diagnostics but is not offered for restore.
            }
        }

        return result;
    }

    public SnapshotSummary Restore(string archivePath)
    {
        var fullArchivePath = ValidateArchivePath(archivePath);
        var manifest = ReadManifest(fullArchivePath);
        var target = NormalizeTarget(manifest.Target);
        var companyTarget = target == "company";
        var safetySnapshot = companyTarget
            ? CreateSnapshotCore("before-restore", "company", fullArchivePath)
            : CreateSnapshotCore("before-restore", "personal", fullArchivePath);
        try
        {
            ApplySnapshot(fullArchivePath, manifest);
            return safetySnapshot;
        }
        catch (Exception restoreException)
        {
            try
            {
                ApplySnapshot(
                    safetySnapshot.ArchivePath,
                    ReadManifest(safetySnapshot.ArchivePath));
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    $"快照恢复失败且自动回滚不完整；安全快照：{safetySnapshot.ArchivePath}",
                    restoreException,
                    rollbackException);
            }

            ExceptionDispatchInfo.Capture(restoreException).Throw();
            throw;
        }
    }

    private void ApplySnapshot(
        string archivePath,
        ProfileSnapshotManifest manifest)
    {
        var target = NormalizeTarget(manifest.Target);
        var companyTarget = target == "company";
        var stagingRoot = Path.Combine(paths.OperationStagingRoot, "restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingRoot);

        try
        {
            ExtractAndValidate(archivePath, stagingRoot, manifest);
            var stagedProfile = Path.Combine(stagingRoot, "profile");
            RestoreManagedSkills(
                Path.Combine(stagedProfile, "skills"),
                companyTarget ? paths.CompanySkills : paths.PersonalSkills);
            ReplaceDirectory(
                Path.Combine(stagedProfile, "memories"),
                companyTarget ? paths.CompanyMemories : paths.PersonalMemories);

            RestoreGlobalRules(
                stagedProfile,
                companyTarget ? paths.CompanyCodexHome : paths.PersonalCodexHome,
                manifest,
                companyTarget);

            if (companyTarget)
            {
                if (manifest.HasConfig)
                {
                    AtomicCopyIfChanged(Path.Combine(stagedProfile, "config.toml"), paths.CompanyConfig);
                }
                else if (File.Exists(paths.CompanyConfig))
                {
                    if ((File.GetAttributes(paths.CompanyConfig) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidOperationException(
                            "工作空间配置目标是链接或重解析点，已拒绝恢复。");
                    }

                    File.Delete(paths.CompanyConfig);
                }
            }
        }
        finally
        {
            SafeDeleteDirectory(stagingRoot);
        }
    }

    private static void RestoreGlobalRules(
        string stagedProfile,
        string targetHome,
        ProfileSnapshotManifest manifest,
        bool legacyCompanyTarget)
    {
        Directory.CreateDirectory(targetHome);
        if (manifest.GlobalRulesCaptured == true)
        {
            foreach (var fileName in ProfileContentPolicy.GlobalRuleFileNames)
            {
                RestoreCapturedRuleFile(stagedProfile, targetHome, fileName);
            }

            return;
        }

        if (manifest.GlobalRulesCaptured is not null || !legacyCompanyTarget)
        {
            return;
        }

        // Legacy company snapshots managed AGENTS.md only. Old personal snapshots never
        // captured global rules, so they must leave the current personal files untouched.
        var stagedAgents = Path.Combine(stagedProfile, "AGENTS.md");
        var targetAgents = Path.Combine(targetHome, "AGENTS.md");
        if (manifest.HasAgents)
        {
            AtomicCopyIfChanged(stagedAgents, targetAgents);
        }
        else if (File.Exists(targetAgents))
        {
            File.Delete(targetAgents);
        }
    }

    private static void RestoreCapturedRuleFile(string stagedProfile, string targetHome, string fileName)
    {
        if (!ProfileContentPolicy.IsGlobalRulePath(fileName))
        {
            throw new InvalidDataException("快照包含不受支持的全局规则文件。");
        }

        var staged = Path.Combine(stagedProfile, fileName);
        var target = Path.Combine(targetHome, fileName);
        if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("全局规则目标是链接或重解析点，已拒绝恢复。");
        }

        if (File.Exists(staged))
        {
            AtomicCopyIfChanged(staged, target);
        }
        else if (File.Exists(target))
        {
            File.Delete(target);
        }
    }

    public string GetSnapshotTarget(string archivePath)
    {
        var fullArchivePath = ValidateArchivePath(archivePath);
        return NormalizeTarget(ReadManifest(fullArchivePath).Target);
    }

    public static string RunGlobalRulesSelfTest()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "CodexChannelLauncher",
            "global-rules-snapshot-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            var staged = Path.Combine(root, "staged");
            var personalTarget = Path.Combine(root, "personal");
            var companyTarget = Path.Combine(root, "company");
            Directory.CreateDirectory(staged);
            Directory.CreateDirectory(personalTarget);
            Directory.CreateDirectory(companyTarget);

            var oldPersonal = new ProfileSnapshotManifest(
                "old-personal",
                DateTime.UtcNow,
                "compatibility",
                false,
                false,
                [],
                "personal");
            File.WriteAllText(Path.Combine(personalTarget, "AGENTS.md"), "keep-personal");
            RestoreGlobalRules(staged, personalTarget, oldPersonal, legacyCompanyTarget: false);
            if (File.ReadAllText(Path.Combine(personalTarget, "AGENTS.md")) != "keep-personal")
            {
                throw new InvalidDataException("旧个人快照错误修改了现有全局规则。");
            }

            File.WriteAllText(Path.Combine(staged, "AGENTS.override.md"), "captured-override");
            File.WriteAllText(Path.Combine(personalTarget, "AGENTS.md"), "remove-me");
            File.WriteAllText(Path.Combine(personalTarget, "AGENTS.override.md"), "old-override");
            var capturedPersonal = oldPersonal with { Id = "new-personal", GlobalRulesCaptured = true };
            RestoreGlobalRules(staged, personalTarget, capturedPersonal, legacyCompanyTarget: false);
            if (File.Exists(Path.Combine(personalTarget, "AGENTS.md")) ||
                File.ReadAllText(Path.Combine(personalTarget, "AGENTS.override.md")) != "captured-override")
            {
                throw new InvalidDataException("新个人快照未准确恢复全局规则存在状态。");
            }

            File.WriteAllText(Path.Combine(staged, "AGENTS.md"), "legacy-company");
            File.WriteAllText(Path.Combine(companyTarget, "AGENTS.md"), "old-company");
            File.WriteAllText(Path.Combine(companyTarget, "AGENTS.override.md"), "keep-company-override");
            var oldCompany = oldPersonal with
            {
                Id = "old-company",
                Target = "company",
                HasAgents = true
            };
            RestoreGlobalRules(staged, companyTarget, oldCompany, legacyCompanyTarget: true);
            if (File.ReadAllText(Path.Combine(companyTarget, "AGENTS.md")) != "legacy-company" ||
                File.ReadAllText(Path.Combine(companyTarget, "AGENTS.override.md")) != "keep-company-override")
            {
                throw new InvalidDataException("旧工作空间快照兼容恢复失败。");
            }
        }
        finally
        {
            SafeDeleteDirectory(root);
        }

        return "新快照可恢复两份全局规则；旧个人快照不触碰规则，旧工作空间快照保持原有 AGENTS.md 语义。";
    }

    private void RestoreManagedSkills(string stagedSkills, string targetSkills)
    {
        Directory.CreateDirectory(stagedSkills);
        Directory.CreateDirectory(targetSkills);
        var backupRoot = Path.Combine(paths.OperationStagingRoot, "skills-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backupRoot);

        var existing = Directory.EnumerateDirectories(targetSkills)
            .Where(directory => !Path.GetFileName(directory).Equals(".system", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var installed = new List<string>();

        try
        {
            foreach (var directory in existing)
            {
                Directory.Move(directory, Path.Combine(backupRoot, Path.GetFileName(directory)));
            }

            foreach (var directory in Directory.EnumerateDirectories(stagedSkills))
            {
                var destination = Path.Combine(targetSkills, Path.GetFileName(directory));
                CopyDirectory(directory, destination);
                installed.Add(destination);
            }

            SafeDeleteDirectory(backupRoot);
        }
        catch
        {
            foreach (var directory in installed)
            {
                SafeDeleteDirectory(directory);
            }

            foreach (var directory in Directory.EnumerateDirectories(backupRoot))
            {
                Directory.Move(directory, Path.Combine(targetSkills, Path.GetFileName(directory)));
            }

            throw;
        }
    }

    private void ReplaceDirectory(string stagedDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(stagedDirectory);
        var backup = Path.Combine(paths.OperationStagingRoot, "directory-backup-" + Guid.NewGuid().ToString("N"));
        var targetExisted = Directory.Exists(targetDirectory);

        try
        {
            if (targetExisted)
            {
                Directory.Move(targetDirectory, backup);
            }

            CopyDirectory(stagedDirectory, targetDirectory);
            SafeDeleteDirectory(backup);
        }
        catch
        {
            SafeDeleteDirectory(targetDirectory);
            if (Directory.Exists(backup))
            {
                Directory.Move(backup, targetDirectory);
            }

            throw;
        }
    }

    private static void ExtractAndValidate(
        string archivePath,
        string destinationRoot,
        ProfileSnapshotManifest manifest)
    {
        var destinationRootFullPath = Path.GetFullPath(destinationRoot);
        var destinationRootPrefix =
            destinationRootFullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var expectedFiles = manifest.Files.ToDictionary(
            record => NormalizeArchivePath(record.Path),
            StringComparer.OrdinalIgnoreCase);
        var extractedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)))
        {
            var archivePathName = NormalizeArchivePath(entry.FullName);
            if (archivePathName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!expectedFiles.ContainsKey(archivePathName) || !extractedFiles.Add(archivePathName))
            {
                throw new InvalidDataException("快照包含未登记或重复的文件，已拒绝恢复。");
            }

            var destination = Path.GetFullPath(Path.Combine(destinationRootPrefix,
                archivePathName.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(destinationRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("快照包含越界路径，已拒绝恢复。");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, false);
        }

        foreach (var record in manifest.Files)
        {
            var archivePathName = NormalizeArchivePath(record.Path);
            var filePath = Path.GetFullPath(Path.Combine(destinationRootPrefix,
                archivePathName.Replace('/', Path.DirectorySeparatorChar)));
            if (!filePath.StartsWith(destinationRootPrefix, StringComparison.OrdinalIgnoreCase) ||
                !extractedFiles.Contains(archivePathName) ||
                !File.Exists(filePath) || new FileInfo(filePath).Length != record.Length ||
                !ComputeSha256(filePath).Equals(record.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"快照文件校验失败：{record.Path}");
            }
        }
    }

    private static void AddDirectory(
        ZipArchive archive,
        string sourceRoot,
        string destinationRoot,
        ICollection<SnapshotFileRecord> records,
        Func<string, bool> include)
    {
        if (!Directory.Exists(sourceRoot))
        {
            return;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        foreach (var filePath in Directory.EnumerateFiles(sourceRoot, "*", options))
        {
            var relative = Path.GetRelativePath(sourceRoot, filePath);
            if (!include(relative))
            {
                continue;
            }

            AddFile(archive, filePath, destinationRoot + "/" + relative.Replace('\\', '/'), records);
        }
    }

    private static void AddFile(
        ZipArchive archive,
        string sourcePath,
        string destinationPath,
        ICollection<SnapshotFileRecord> records)
    {
        if (!File.Exists(sourcePath))
        {
            return;
        }

        var entry = archive.CreateEntry(destinationPath, CompressionLevel.Optimal);
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var output = entry.Open();
        input.CopyTo(output);
        records.Add(new SnapshotFileRecord(destinationPath, input.Length, ComputeSha256(sourcePath)));
    }

    private static ProfileSnapshotManifest ReadManifest(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.GetEntry("manifest.json")
                    ?? throw new InvalidDataException("快照缺少 manifest.json。");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return JsonSerializer.Deserialize<ProfileSnapshotManifest>(reader.ReadToEnd(), JsonOptions)
               ?? throw new InvalidDataException("快照清单不可解析。");
    }

    private static SnapshotSummary ToSummary(string archivePath, ProfileSnapshotManifest manifest) =>
        new(
            manifest.Id,
            manifest.CreatedAtUtc,
            manifest.Reason,
            archivePath,
            manifest.Files.Count,
            manifest.Files.Sum(file => file.Length),
            NormalizeTarget(manifest.Target));

    private string ValidateArchivePath(string archivePath)
    {
        var fullArchivePath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullArchivePath) || !LauncherPaths.IsUnder(fullArchivePath, paths.SnapshotDirectory))
        {
            throw new InvalidOperationException("只能恢复启动器快照目录中的有效快照。");
        }

        return fullArchivePath;
    }

    private void PruneOldSnapshots(string? protectedArchivePath = null)
    {
        var ordered = Directory.EnumerateFiles(paths.SnapshotDirectory, "*.zip")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();
        var protectedFullPath = protectedArchivePath is null
            ? null
            : Path.GetFullPath(protectedArchivePath);
        var retained = ordered
            .Where(path => protectedFullPath is null ||
                           !Path.GetFullPath(path).Equals(
                               protectedFullPath,
                               StringComparison.OrdinalIgnoreCase))
            .Take(protectedFullPath is null ? RetentionCount : RetentionCount - 1)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (protectedFullPath is not null)
        {
            retained.Add(protectedFullPath);
        }

        foreach (var archive in ordered.Where(path => !retained.Contains(Path.GetFullPath(path))))
        {
            try
            {
                File.Delete(archive);
            }
            catch
            {
                // Retention cleanup is best effort; never fail the snapshot itself.
            }
        }
    }

    private static bool IsSystemSkill(string relativePath)
    {
        var firstSegment = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return firstSegment.Equals(".system", StringComparison.OrdinalIgnoreCase);
    }

    internal static void AtomicCopy(string source, string destination)
    {
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("所需文件不存在。", source);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var targetStream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                sourceStream.CopyTo(targetStream);
                targetStream.Flush(true);
            }

            File.Move(temporary, destination, true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static void AtomicCopyIfChanged(string source, string destination)
    {
        if (File.Exists(destination) &&
            new FileInfo(source).Length == new FileInfo(destination).Length &&
            ComputeSha256(source).Equals(
                ComputeSha256(destination),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        AtomicCopy(source, destination);
    }

    internal static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        foreach (var sourceFile in Directory.EnumerateFiles(source, "*", options))
        {
            var relative = Path.GetRelativePath(source, sourceFile);
            var targetFile = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(sourceFile, targetFile, false);
        }
    }

    internal static void SafeDeleteDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        Directory.Delete(directory, true);
    }

    internal static string ComputeSha256(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string Sanitize(string value)
    {
        var result = new string(value.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '-')
            .ToArray()).Trim('-');
        return string.IsNullOrWhiteSpace(result) ? "snapshot" : result;
    }

    private static string NormalizeTarget(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("company", StringComparison.OrdinalIgnoreCase))
        {
            return "company";
        }

        if (value.Equals("personal", StringComparison.OrdinalIgnoreCase))
        {
            return "personal";
        }

        throw new InvalidDataException("快照目标无效，已拒绝恢复。");
    }

    private static string NormalizeArchivePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value[0] is '/' or '\\')
        {
            throw new InvalidDataException("快照包含无效路径，已拒绝恢复。");
        }

        var normalized = value.Replace('\\', '/');
        if (normalized.Contains(':') ||
            normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or "..") ||
            (!normalized.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) &&
             !normalized.StartsWith("profile/", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("快照包含无效路径，已拒绝恢复。");
        }

        return normalized;
    }
}
