using System.Text;

namespace CodexChannelLauncher.Core;

public sealed class ProfileMergeService(
    LauncherPaths paths,
    CompanyProfileManager profileManager,
    ProfileSnapshotService snapshots)
{
    private const string OperationGateName = @"Local\CodexChannelLauncher.ConfigurationOperation";
    private MergeBaseStore BaseStore => new(paths.MergeBaseDirectory);

    public bool IsCompanyRunning() => ProcessInventory.GetChatGptRoots()
        .Any(process => LauncherPaths.IsUnder(process.ExecutablePath, paths.RuntimeCacheRoot));

    public bool IsPersonalRunning() => ProcessInventory.GetChatGptRoots()
        .Any(process => !LauncherPaths.IsUnder(process.ExecutablePath, paths.RuntimeCacheRoot));

    public IReadOnlyList<MergeFileEntry> GetFiles(MergeResourceKind kind, string containerName)
    {
        EnsureProfileReady();
        var roots = ResolveRoots(kind, containerName);
        var personal = EnumerateFiles(roots.PersonalRoot, kind);
        var company = EnumerateFiles(roots.CompanyRoot, kind);
        return personal.Keys.Union(company.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(relativePath =>
            {
                personal.TryGetValue(relativePath, out var personalFile);
                company.TryGetValue(relativePath, out var companyFile);
                var state = personalFile is null
                    ? ComparisonState.CompanyOnly
                    : companyFile is null
                        ? ComparisonState.PersonalOnly
                        : personalFile.Fingerprint.Equals(
                            companyFile.Fingerprint,
                            StringComparison.OrdinalIgnoreCase)
                            ? ComparisonState.Same
                            : ComparisonState.Different;
                return new MergeFileEntry(
                    relativePath,
                    state,
                    personalFile is not null,
                    companyFile is not null,
                    personalFile?.Length ?? 0,
                    companyFile?.Length ?? 0,
                    BaseStore.Has(kind, roots.ContainerName, relativePath),
                    kind == MergeResourceKind.Memories &&
                    ProfileContentPolicy.IsGeneratedMemoryAggregate(relativePath));
            })
            .Where(file => file.State != ComparisonState.Same)
            .ToArray();
    }

    public MergeDocument LoadDocument(
        MergeResourceKind kind,
        string containerName,
        string relativePath)
    {
        EnsureProfileReady();
        var roots = ResolveRoots(kind, containerName);
        var normalized = NormalizeRelativePath(kind, relativePath);
        var personalPath = ResolveFilePath(roots.PersonalRoot, normalized);
        var companyPath = ResolveFilePath(roots.CompanyRoot, normalized);
        var personal = TextFileCodec.Read(personalPath);
        var company = TextFileCodec.Read(companyPath);
        if (!personal.Exists && !company.Exists)
        {
            throw new FileNotFoundException("个人与工作空间都不存在该差异文件。", normalized);
        }

        var mergeBase = BaseStore.TryLoad(kind, roots.ContainerName, normalized);
        var isText = (!personal.Exists || personal.IsText) &&
                     (!company.Exists || company.IsText);
        TextMergePlan? plan = null;
        if (isText)
        {
            plan = ThreeWayTextMerge.Build(
                mergeBase is not null,
                mergeBase?.Exists == true,
                mergeBase?.Content ?? string.Empty,
                personal.Exists,
                personal.Text,
                company.Exists,
                company.Text);
        }

        var warning = BuildWarning(kind, normalized, personal, company, mergeBase is not null, plan);
        return new MergeDocument(
            kind,
            roots.ContainerName,
            normalized,
            isText,
            personal.Exists,
            company.Exists,
            personal.Length,
            company.Length,
            personal.Fingerprint,
            company.Fingerprint,
            personal.EncodingDisplayName,
            company.EncodingDisplayName,
            warning,
            plan);
    }

    public MergeWriteResult Save(MergeSaveRequest request)
    {
        EnsureProfileReady();
        EnsureBothAppsStopped();
        return WithOperationGate(() =>
        {
            EnsureBothAppsStopped();
            var roots = ResolveRoots(request.ResourceKind, request.ContainerName);
            var normalized = NormalizeRelativePath(request.ResourceKind, request.RelativePath);
            var personalPath = ResolveFilePath(roots.PersonalRoot, normalized);
            var companyPath = ResolveFilePath(roots.CompanyRoot, normalized);
            var personal = TextFileCodec.Read(personalPath);
            var company = TextFileCodec.Read(companyPath);
            ValidateExpectedFingerprints(request, personal, company);
            var intents = BuildWriteIntents(request, personalPath, companyPath, personal, company);
            var snapshotsCreated = CreateTargetSnapshots(request.Target);
            ApplyIntentsAtomically(
                intents,
                paths.OperationStagingRoot,
                () =>
                {
                    ValidateExpectedFingerprint(
                        "个人",
                        request.ExpectedPersonalFingerprint,
                        TextFileCodec.ComputeFingerprint(personalPath));
                    ValidateExpectedFingerprint(
                        "工作空间",
                        request.ExpectedCompanyFingerprint,
                        TextFileCodec.ComputeFingerprint(companyPath));
                });

            var baseUpdated = false;
            string? baseWarning = null;
            try
            {
                var currentPersonal = TextFileCodec.Read(personalPath);
                var currentCompany = TextFileCodec.Read(companyPath);
                if (currentPersonal.Exists == currentCompany.Exists &&
                    (!currentPersonal.Exists ||
                     currentPersonal.IsText && currentCompany.IsText &&
                     currentPersonal.Text.Equals(currentCompany.Text, StringComparison.Ordinal)))
                {
                    BaseStore.Save(
                        request.ResourceKind,
                        roots.ContainerName,
                        normalized,
                        currentPersonal.Exists,
                        currentPersonal.Text);
                    baseUpdated = true;
                }
            }
            catch (Exception exception)
            {
                baseWarning = "文件已安全写入，但共同基线记录失败：" + exception.Message;
            }

            var targetText = request.Target switch
            {
                MergeWriteTarget.Personal => "个人",
                MergeWriteTarget.Company => "工作空间",
                MergeWriteTarget.Both => "个人与工作空间",
                _ => "目标"
            };
            return new MergeWriteResult(
                snapshotsCreated,
                baseUpdated,
                baseWarning ?? $"已写入{targetText}空间。" +
                (baseUpdated ? "双方内容一致，已更新三方共同基线。" : "双方仍有差异，保留原共同基线。"));
        });
    }

    public static string RunEngineSelfTest()
    {
        var automatic = ThreeWayTextMerge.Build(
            true,
            true,
            "A\nB\nC",
            true,
            "A\nP\nC",
            true,
            "A\nB\nQ");
        var automaticResult = ThreeWayTextMerge.JoinParts(
            automatic.Parts.Select(part => part.SuggestedLines));
        if (automatic.ConflictCount != 0 || automaticResult != "A\nP\nQ")
        {
            throw new InvalidDataException("三方非重叠修改未能自动合并。");
        }

        var conflict = ThreeWayTextMerge.Build(
            true,
            true,
            "A\nB\nC",
            true,
            "A\nP\nC",
            true,
            "A\nQ\nC");
        if (conflict.ConflictCount != 1)
        {
            throw new InvalidDataException("三方重叠修改未被识别为冲突。");
        }

        var insertions = ThreeWayTextMerge.Build(
            true,
            true,
            "A\nB",
            true,
            "A\nX\nB",
            true,
            "A\nB\nY");
        var insertionResult = ThreeWayTextMerge.JoinParts(
            insertions.Parts.Select(part => part.SuggestedLines));
        if (insertions.ConflictCount != 0 || insertionResult != "A\nX\nB\nY")
        {
            throw new InvalidDataException("三方非重叠插入未能自动合并。");
        }

        var overlappingInsertions = ThreeWayTextMerge.Build(
            true,
            true,
            "A\nB",
            true,
            "A\nX\nB",
            true,
            "A\nY\nB");
        if (overlappingInsertions.ConflictCount != 1)
        {
            throw new InvalidDataException("同一位置的双方插入未被识别为冲突。");
        }

        for (var index = 0; index < 40; index++)
        {
            var baseValue = $"L0\nL1\nL2\nL3\nL4-{index}";
            var personalValue = baseValue.Replace("L2", $"P2-{index}", StringComparison.Ordinal);
            var oneSided = ThreeWayTextMerge.Build(
                true,
                true,
                baseValue,
                true,
                personalValue,
                true,
                baseValue);
            var oneSidedResult = ThreeWayTextMerge.JoinParts(
                oneSided.Parts.Select(part => part.SuggestedLines));
            if (oneSided.ConflictCount != 0 || oneSidedResult != personalValue)
            {
                throw new InvalidDataException("单侧修改回归测试失败。");
            }
        }

        var firstDiff = ThreeWayTextMerge.Build(
            false,
            false,
            string.Empty,
            true,
            "personal",
            true,
            "company");
        if (firstDiff.ConflictCount == 0)
        {
            throw new InvalidDataException("首次双向 Diff 未要求人工采纳。");
        }

        if (!ProfileContentPolicy.IsGlobalRulePath("AGENTS.md") ||
            !ProfileContentPolicy.IsGlobalRulePath("AGENTS.override.md") ||
            ProfileContentPolicy.IsGlobalRulePath("nested/AGENTS.md") ||
            ProfileContentPolicy.IsGlobalRulePath("TEAM_GUIDE.md"))
        {
            throw new InvalidDataException("全局规则路径白名单测试失败。");
        }

        var probeRoot = Path.Combine(
            Path.GetTempPath(),
            "CodexChannelLauncher",
            "merge-engine-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(probeRoot);
            var baseDirectory = Path.Combine(probeRoot, "bases");
            var store = new MergeBaseStore(baseDirectory);
            store.Save(MergeResourceKind.Skill, "probe", "SKILL.md", true, "base\nvalue");
            var stored = store.TryLoad(MergeResourceKind.Skill, "probe", "SKILL.md");
            if (stored is null || !stored.Exists || stored.Content != "base\nvalue")
            {
                throw new InvalidDataException("共同基线仓库读写失败。");
            }

            var utf16Path = Path.Combine(probeRoot, "utf16.txt");
            File.WriteAllText(utf16Path, "甲\r\n乙", new UnicodeEncoding(false, true, true));
            var utf16 = TextFileCodec.Read(utf16Path);
            if (!utf16.IsText || utf16.Text != "甲\n乙" || utf16.NewLine != "\r\n" ||
                !File.ReadAllBytes(utf16Path).SequenceEqual(TextFileCodec.Encode(utf16.Text, utf16)))
            {
                throw new InvalidDataException("UTF-16 编码或换行保真测试失败。");
            }

            var utf8BomPath = Path.Combine(probeRoot, "utf8-bom.txt");
            File.WriteAllText(utf8BomPath, "A\nB", new UTF8Encoding(true, true));
            var utf8Bom = TextFileCodec.Read(utf8BomPath);
            if (!utf8Bom.IsText || utf8Bom.EncodingDisplayName != "UTF-8 BOM" ||
                !File.ReadAllBytes(utf8BomPath).SequenceEqual(TextFileCodec.Encode(utf8Bom.Text, utf8Bom)))
            {
                throw new InvalidDataException("UTF-8 BOM 保真测试失败。");
            }

            var binaryPath = Path.Combine(probeRoot, "binary.bin");
            File.WriteAllBytes(binaryPath, [0, 1, 2, 3]);
            if (TextFileCodec.Read(binaryPath).IsText)
            {
                throw new InvalidDataException("二进制文件未降级为整文件模式。");
            }

            var targetA = Path.Combine(probeRoot, "target-a.txt");
            var targetB = Path.Combine(probeRoot, "target-b.txt");
            File.WriteAllText(targetA, "old-a");
            File.WriteAllText(targetB, "old-b");
            ApplyIntentsAtomically(
                [
                    new WriteIntent(targetA, true, Encoding.UTF8.GetBytes("new-a"), null, null),
                    new WriteIntent(targetB, true, Encoding.UTF8.GetBytes("new-b"), null, null)
                ],
                Path.Combine(probeRoot, "staging"),
                null);
            if (File.ReadAllText(targetA) != "new-a" || File.ReadAllText(targetB) != "new-b")
            {
                throw new InvalidDataException("双目标原子写入失败。");
            }

            File.WriteAllText(targetA, "rollback-a");
            File.WriteAllText(targetB, "rollback-b");
            using (var locked = new FileStream(targetB, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try
                {
                    ApplyIntentsAtomically(
                        [
                            new WriteIntent(targetA, true, Encoding.UTF8.GetBytes("bad-a"), null, null),
                            new WriteIntent(targetB, true, Encoding.UTF8.GetBytes("bad-b"), null, null)
                        ],
                        Path.Combine(probeRoot, "staging-rollback"),
                        null);
                    throw new InvalidDataException("锁定文件未触发回滚测试。");
                }
                catch (IOException)
                {
                    // Expected on Windows: target B is locked after target A was committed.
                }
            }

            if (File.ReadAllText(targetA) != "rollback-a" || File.ReadAllText(targetB) != "rollback-b")
            {
                throw new InvalidDataException("双目标失败后未完整回滚。");
            }
        }
        finally
        {
            if (Directory.Exists(probeRoot))
            {
                Directory.Delete(probeRoot, true);
            }
        }

        return "三方自动合并、冲突识别、共同基线、双目标写入与失败回滚均通过。";
    }

    private IReadOnlyList<WriteIntent> BuildWriteIntents(
        MergeSaveRequest request,
        string personalPath,
        string companyPath,
        TextFileSnapshot personal,
        TextFileSnapshot company)
    {
        bool resultExists;
        byte[]? resultBytes = null;
        string? sourcePath = null;
        string? expectedSourceFingerprint = null;
        switch (request.Resolution)
        {
            case MergeResolutionKind.Text:
                if ((!personal.Exists || personal.IsText) && (!company.Exists || company.IsText))
                {
                    resultExists = request.ResultExists;
                    break;
                }

                throw new InvalidOperationException("文件已变为二进制或超大文件，请刷新后整文件采用。");
            case MergeResolutionKind.PersonalFile:
                if (!personal.Exists)
                {
                    throw new FileNotFoundException("个人版本不存在，不能整文件采用。", personalPath);
                }

                resultExists = true;
                sourcePath = personalPath;
                expectedSourceFingerprint = personal.Fingerprint;
                break;
            case MergeResolutionKind.CompanyFile:
                if (!company.Exists)
                {
                    throw new FileNotFoundException("工作空间版本不存在，不能整文件采用。", companyPath);
                }

                resultExists = true;
                sourcePath = companyPath;
                expectedSourceFingerprint = company.Fingerprint;
                break;
            case MergeResolutionKind.Delete:
                resultExists = false;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request.Resolution));
        }

        var targetPaths = request.Target switch
        {
            MergeWriteTarget.Personal => new[] { personalPath },
            MergeWriteTarget.Company => new[] { companyPath },
            MergeWriteTarget.Both => new[] { personalPath, companyPath },
            _ => throw new ArgumentOutOfRangeException(nameof(request.Target))
        };
        var intents = new List<WriteIntent>();
        var sharedTextTemplate = request.Target == MergeWriteTarget.Both &&
                                 request.Resolution == MergeResolutionKind.Text && resultExists
            ? personal.Exists && personal.IsText
                ? personal
                : company.Exists && company.IsText
                    ? company
                    : TextFileSnapshot.Missing
            : null;
        foreach (var targetPath in targetPaths)
        {
            if (request.Resolution == MergeResolutionKind.Text && resultExists)
            {
                var target = targetPath.Equals(personalPath, StringComparison.OrdinalIgnoreCase)
                    ? personal
                    : company;
                var other = targetPath.Equals(personalPath, StringComparison.OrdinalIgnoreCase)
                    ? company
                    : personal;
                var template = sharedTextTemplate ??
                               (target.Exists && target.IsText
                                   ? target
                                   : other.Exists && other.IsText
                                       ? other
                                       : TextFileSnapshot.Missing);
                resultBytes = TextFileCodec.Encode(request.ResultText, template);
            }

            intents.Add(new WriteIntent(
                targetPath,
                resultExists,
                resultBytes,
                sourcePath,
                expectedSourceFingerprint));
        }

        return intents;
    }

    private IReadOnlyList<SnapshotSummary> CreateTargetSnapshots(MergeWriteTarget target) => target switch
    {
        MergeWriteTarget.Personal => [snapshots.CreatePersonalSnapshot("before-line-merge")],
        MergeWriteTarget.Company => [snapshots.CreateSnapshot("before-line-merge")],
        MergeWriteTarget.Both =>
        [
            snapshots.CreatePersonalSnapshot("before-line-merge-both"),
            snapshots.CreateSnapshot("before-line-merge-both")
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };

    private static void ApplyIntentsAtomically(
        IReadOnlyList<WriteIntent> intents,
        string operationStagingRoot,
        Action? validateBeforeCommit)
    {
        if (intents.Count == 0)
        {
            throw new InvalidOperationException("没有可写入的合并目标。");
        }

        Directory.CreateDirectory(operationStagingRoot);
        var transactionRoot = Path.Combine(
            operationStagingRoot,
            "line-merge-" + Guid.NewGuid().ToString("N"));
        var stagedRoot = Path.Combine(transactionRoot, "staged");
        var backupRoot = Path.Combine(transactionRoot, "backup");
        Directory.CreateDirectory(stagedRoot);
        Directory.CreateDirectory(backupRoot);
        var staged = new List<StagedWrite>();
        var applied = new List<AppliedWrite>();
        try
        {
            for (var index = 0; index < intents.Count; index++)
            {
                var intent = intents[index];
                string? stagedPath = null;
                if (intent.Exists)
                {
                    stagedPath = Path.Combine(stagedRoot, index + ".bin");
                    if (intent.Bytes is not null)
                    {
                        File.WriteAllBytes(stagedPath, intent.Bytes);
                    }
                    else if (intent.SourcePath is not null)
                    {
                        File.Copy(intent.SourcePath, stagedPath, false);
                        if (intent.ExpectedSourceFingerprint is not null &&
                            !TextFileCodec.ComputeFingerprint(stagedPath).Equals(
                                intent.ExpectedSourceFingerprint,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            throw new IOException("来源文件在暂存期间发生变化，已取消写入。");
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException("合并结果缺少暂存内容。");
                    }
                }

                staged.Add(new StagedWrite(intent.TargetPath, intent.Exists, stagedPath));
            }

            validateBeforeCommit?.Invoke();
            for (var index = 0; index < staged.Count; index++)
            {
                var item = staged[index];
                var backupPath = Path.Combine(backupRoot, index + ".bin");
                Directory.CreateDirectory(Path.GetDirectoryName(item.TargetPath)!);
                var hadOriginal = File.Exists(item.TargetPath);
                if (hadOriginal)
                {
                    File.Move(item.TargetPath, backupPath);
                }

                try
                {
                    if (item.Exists)
                    {
                        File.Move(item.StagedPath!, item.TargetPath);
                    }

                    applied.Add(new AppliedWrite(item.TargetPath, backupPath, hadOriginal));
                }
                catch
                {
                    if (File.Exists(item.TargetPath))
                    {
                        File.Delete(item.TargetPath);
                    }

                    if (hadOriginal && File.Exists(backupPath))
                    {
                        File.Move(backupPath, item.TargetPath);
                    }

                    throw;
                }
            }
        }
        catch
        {
            foreach (var item in applied.AsEnumerable().Reverse())
            {
                if (File.Exists(item.TargetPath))
                {
                    File.Delete(item.TargetPath);
                }

                if (item.HadOriginal && File.Exists(item.BackupPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(item.TargetPath)!);
                    File.Move(item.BackupPath, item.TargetPath);
                }
            }

            throw;
        }
        finally
        {
            try
            {
                ProfileSnapshotService.SafeDeleteDirectory(transactionRoot);
            }
            catch
            {
                // Cleanup is best effort after commit/rollback; never mask the actual transaction result.
            }
        }
    }

    private static void ValidateExpectedFingerprints(
        MergeSaveRequest request,
        TextFileSnapshot personal,
        TextFileSnapshot company)
    {
        ValidateExpectedFingerprint(
            "个人",
            request.ExpectedPersonalFingerprint,
            personal.Fingerprint);
        ValidateExpectedFingerprint(
            "工作空间",
            request.ExpectedCompanyFingerprint,
            company.Fingerprint);
    }

    private static void ValidateExpectedFingerprint(string side, string expected, string actual)
    {
        if (!expected.Equals(actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{side}文件自打开工作台后已发生变化，请刷新差异后重新解决。");
        }
    }

    private ResourceRoots ResolveRoots(MergeResourceKind kind, string containerName)
    {
        if (kind == MergeResourceKind.GlobalRules)
        {
            return new ResourceRoots(paths.PersonalCodexHome, paths.CompanyCodexHome, "global-rules");
        }

        if (kind == MergeResourceKind.Memories)
        {
            return new ResourceRoots(paths.PersonalMemories, paths.CompanyMemories, "memories");
        }

        if (string.IsNullOrWhiteSpace(containerName) ||
            containerName.Equals(".system", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("请选择有效的用户 Skill。", nameof(containerName));
        }

        return new ResourceRoots(
            ResolveChildDirectory(paths.PersonalSkills, containerName),
            ResolveChildDirectory(paths.CompanySkills, containerName),
            containerName);
    }

    private static Dictionary<string, FileProbe> EnumerateFiles(string root, MergeResourceKind kind)
    {
        if (!Directory.Exists(root))
        {
            return new Dictionary<string, FileProbe>(StringComparer.OrdinalIgnoreCase);
        }

        EnsureNoReparsePoint(root);

        if (kind == MergeResourceKind.GlobalRules)
        {
            return ProfileContentPolicy.GlobalRuleFileNames
                .Select(name => new { Name = name, Path = Path.Combine(root, name) })
                .Where(item => File.Exists(item.Path))
                .ToDictionary(
                    item => item.Name,
                    item =>
                    {
                        EnsureNoReparsePoint(item.Path);
                        return new FileProbe(
                            new FileInfo(item.Path).Length,
                            TextFileCodec.ComputeFingerprint(item.Path));
                    },
                    StringComparer.OrdinalIgnoreCase);
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        return Directory.EnumerateFiles(root, "*", options)
            .Select(path => new
            {
                Path = path,
                Relative = Path.GetRelativePath(root, path).Replace('\\', '/')
            })
            .Where(item => kind != MergeResourceKind.Memories ||
                           ProfileContentPolicy.IsManagedMemoryPath(item.Relative))
            .ToDictionary(
                item => item.Relative,
                item => new FileProbe(
                    new FileInfo(item.Path).Length,
                    TextFileCodec.ComputeFingerprint(item.Path)),
                StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeRelativePath(MergeResourceKind kind, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("差异文件路径无效。", nameof(relativePath));
        }

        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment =>
                segment is "." or ".." ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new ArgumentException("差异文件路径包含越界片段。", nameof(relativePath));
        }

        if (kind == MergeResourceKind.Memories &&
            !ProfileContentPolicy.IsManagedMemoryPath(normalized))
        {
            throw new InvalidOperationException("该 Memories 文件属于运行时或内部目录，不参与合并。");
        }

        if (kind == MergeResourceKind.GlobalRules &&
            !ProfileContentPolicy.IsGlobalRulePath(normalized))
        {
            throw new InvalidOperationException("全局规则合并只允许 AGENTS.override.md 与 AGENTS.md。");
        }

        return string.Join('/', segments);
    }

    private static string ResolveFilePath(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(
            fullRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!LauncherPaths.IsUnder(fullPath, fullRoot))
        {
            throw new InvalidOperationException("合并文件路径越界。");
        }

        EnsureNoReparsePoint(fullPath);
        return fullPath;
    }

    private static void EnsureNoReparsePoint(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var pathRoot = Path.GetPathRoot(fullPath) ??
                       throw new InvalidOperationException("无法解析合并路径根目录。");
        var current = pathRoot;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("合并路径包含链接或重解析点，已拒绝访问。");
        }

        var relative = Path.GetRelativePath(pathRoot, fullPath);
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("合并路径包含链接或重解析点，已拒绝访问。");
            }
        }
    }

    private static string ResolveChildDirectory(string root, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains(Path.DirectorySeparatorChar) ||
            name.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("Skill 名称不是安全的目录名。", nameof(name));
        }

        var path = Path.GetFullPath(Path.Combine(root, name));
        if (!LauncherPaths.IsUnder(path, root))
        {
            throw new InvalidOperationException("Skill 路径越界。");
        }

        return path;
    }

    private void EnsureProfileReady()
    {
        if (profileManager.IsInitialized)
        {
            return;
        }

        if (IsCompanyRunning())
        {
            throw new InvalidOperationException("工作空间 Codex App 正在运行，暂不能完成配置接管。");
        }

        profileManager.EnsureInitialized();
    }

    private void EnsureBothAppsStopped()
    {
        var companyRunning = IsCompanyRunning();
        var personalRunning = IsPersonalRunning();
        if (!companyRunning && !personalRunning)
        {
            return;
        }

        var running = companyRunning && personalRunning
            ? "个人与工作空间 Codex App"
            : companyRunning
                ? "工作空间 Codex App"
                : "个人 Codex App";
        throw new InvalidOperationException($"逐块写回前请先完全退出{running}，工作台仍可保持打开并刷新。");
    }

    private T WithOperationGate<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, OperationGateName);
        var lockTaken = false;
        try
        {
            try
            {
                lockTaken = mutex.WaitOne(TimeSpan.FromSeconds(45));
            }
            catch (AbandonedMutexException)
            {
                lockTaken = true;
            }

            if (!lockTaken)
            {
                throw new TimeoutException("另一个多开器窗口正在修改配置或双向数据。");
            }

            return action();
        }
        finally
        {
            if (lockTaken)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static string BuildWarning(
        MergeResourceKind kind,
        string relativePath,
        TextFileSnapshot personal,
        TextFileSnapshot company,
        bool hasBase,
        TextMergePlan? plan)
    {
        var messages = new List<string>();
        if (kind == MergeResourceKind.Memories &&
            ProfileContentPolicy.IsGeneratedMemoryAggregate(relativePath))
        {
            messages.Add("这是聚合/索引型记忆文件；逐块修改可能暂时与底层记录不一致，优先整文件采用权威侧。");
        }

        else if (kind == MergeResourceKind.GlobalRules)
        {
            messages.Add(relativePath.Equals("AGENTS.override.md", StringComparison.OrdinalIgnoreCase)
                ? "非空 AGENTS.override.md 会优先于同一 CODEX_HOME 下的 AGENTS.md。"
                : "当同一 CODEX_HOME 存在非空 AGENTS.override.md 时，AGENTS.md 会作为后备文件而暂不生效。");
        }

        if (!personal.IsText && personal.Exists)
        {
            messages.Add("个人版本：" + personal.FailureReason);
        }

        if (!company.IsText && company.Exists)
        {
            messages.Add("工作空间版本：" + company.FailureReason);
        }

        if (plan is not null && plan.Parts.All(part => !part.IsChanged) &&
            !personal.Fingerprint.Equals(company.Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            messages.Add("文本内容相同，但编码、BOM 或换行格式不同；需要一致字节时请整文件采用一侧。");
        }

        if (!hasBase)
        {
            messages.Add("这是首次双向 Diff：没有共同基线，所有文本差异都需要人工确认；双方收敛后会建立三方基线。");
        }

        return string.Join(" ", messages);
    }

    private sealed record ResourceRoots(string PersonalRoot, string CompanyRoot, string ContainerName);

    private sealed record FileProbe(long Length, string Fingerprint);

    private sealed record WriteIntent(
        string TargetPath,
        bool Exists,
        byte[]? Bytes,
        string? SourcePath,
        string? ExpectedSourceFingerprint);

    private sealed record StagedWrite(string TargetPath, bool Exists, string? StagedPath);

    private sealed record AppliedWrite(string TargetPath, string BackupPath, bool HadOriginal);
}
