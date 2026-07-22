namespace CodexChannelLauncher.Core;

public enum MergeResourceKind
{
    Skill,
    GlobalRules,
    Memories
}

public enum MergeWriteTarget
{
    Company,
    Personal,
    Both
}

public enum MergeResolutionKind
{
    Text,
    PersonalFile,
    CompanyFile,
    Delete
}

public enum TextMergePartKind
{
    Unchanged,
    AutoPersonal,
    AutoCompany,
    AutoSame,
    Conflict
}

public sealed record MergeFileEntry(
    string RelativePath,
    ComparisonState State,
    bool PersonalExists,
    bool CompanyExists,
    long PersonalBytes,
    long CompanyBytes,
    bool HasMergeBase,
    bool IsGeneratedAggregate)
{
    public string StatusText => State switch
    {
        ComparisonState.Different => "内容冲突",
        ComparisonState.PersonalOnly => "仅个人存在",
        ComparisonState.CompanyOnly => "仅工作空间存在",
        _ => "一致"
    };

    public string DetailText =>
        $"个人 {FormatBytes(PersonalBytes)} · 工作空间 {FormatBytes(CompanyBytes)}" +
        (HasMergeBase ? " · 有共同基线" : " · 首次双向 Diff");

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024d / 1024d:F1} MB",
        >= 1024 => $"{bytes / 1024d:F1} KB",
        _ => $"{bytes} B"
    };
}

public sealed record TextMergePart(
    int Index,
    TextMergePartKind Kind,
    IReadOnlyList<string> BaseLines,
    IReadOnlyList<string> PersonalLines,
    IReadOnlyList<string> CompanyLines,
    IReadOnlyList<string> SuggestedLines)
{
    public bool IsChanged => Kind != TextMergePartKind.Unchanged;

    public bool RequiresResolution => Kind == TextMergePartKind.Conflict;

    public string StatusText => Kind switch
    {
        TextMergePartKind.Unchanged => "未变化",
        TextMergePartKind.AutoPersonal => "自动采用个人修改",
        TextMergePartKind.AutoCompany => "自动采用工作空间修改",
        TextMergePartKind.AutoSame => "双方修改结果一致",
        TextMergePartKind.Conflict => "需要人工解决",
        _ => "未知"
    };
}

public sealed record TextMergePlan(
    bool HasBase,
    bool BaseExists,
    bool PersonalExists,
    bool CompanyExists,
    bool SuggestedExists,
    bool ExistenceRequiresResolution,
    string ExistenceStatusText,
    IReadOnlyList<TextMergePart> Parts)
{
    public int ConflictCount => Parts.Count(part => part.RequiresResolution) +
                                (ExistenceRequiresResolution ? 1 : 0);
}

public sealed record MergeDocument(
    MergeResourceKind ResourceKind,
    string ContainerName,
    string RelativePath,
    bool IsText,
    bool PersonalExists,
    bool CompanyExists,
    long PersonalBytes,
    long CompanyBytes,
    string PersonalFingerprint,
    string CompanyFingerprint,
    string PersonalEncoding,
    string CompanyEncoding,
    string WarningText,
    TextMergePlan? TextPlan);

public sealed record MergeSaveRequest(
    MergeResourceKind ResourceKind,
    string ContainerName,
    string RelativePath,
    MergeWriteTarget Target,
    MergeResolutionKind Resolution,
    bool ResultExists,
    string ResultText,
    string ExpectedPersonalFingerprint,
    string ExpectedCompanyFingerprint);

public sealed record MergeWriteResult(
    IReadOnlyList<SnapshotSummary> SafetySnapshots,
    bool MergeBaseUpdated,
    string Detail);
