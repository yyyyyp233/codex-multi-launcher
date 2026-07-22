namespace CodexChannelLauncher.Core;

public static class ThreeWayTextMerge
{
    private const long MaximumLcsCells = 8_000_000;

    public static TextMergePlan Build(
        bool hasBase,
        bool baseExists,
        string baseText,
        bool personalExists,
        string personalText,
        bool companyExists,
        string companyText)
    {
        var baseLines = ToLines(baseExists, baseText);
        var personalLines = ToLines(personalExists, personalText);
        var companyLines = ToLines(companyExists, companyText);
        var parts = hasBase
            ? BuildThreeWay(baseLines, personalLines, companyLines)
            : BuildTwoWay(personalLines, companyLines);

        var (suggestedExists, existenceRequiresResolution, existenceStatus) = ResolveExistence(
            hasBase,
            baseExists,
            personalExists,
            companyExists);
        return new TextMergePlan(
            hasBase,
            baseExists,
            personalExists,
            companyExists,
            suggestedExists,
            existenceRequiresResolution,
            existenceStatus,
            parts);
    }

    public static string JoinParts(IEnumerable<IReadOnlyList<string>> parts) =>
        string.Join("\n", parts.SelectMany(lines => lines));

    public static IReadOnlyList<string> TextToPartLines(string text) =>
        text.Length == 0 ? [] : TextFileCodec.NormalizeNewLines(text).Split('\n');

    public static string PartLinesToText(IReadOnlyList<string> lines) =>
        string.Join("\n", lines);

    private static IReadOnlyList<TextMergePart> BuildTwoWay(
        IReadOnlyList<string> personal,
        IReadOnlyList<string> company)
    {
        if (personal.SequenceEqual(company, StringComparer.Ordinal))
        {
            return [CreatePart(0, TextMergePartKind.Unchanged, [], personal, company, personal)];
        }

        var edits = CreateEdits(personal, company);
        var parts = new List<TextMergePart>();
        var cursor = 0;
        foreach (var edit in edits)
        {
            AddUnchanged(parts, personal, cursor, edit.Start);
            var personalLines = Slice(personal, edit.Start, edit.End);
            parts.Add(CreatePart(
                parts.Count,
                TextMergePartKind.Conflict,
                [],
                personalLines,
                edit.Replacement,
                personalLines));
            cursor = edit.End;
        }

        AddUnchanged(parts, personal, cursor, personal.Count);
        return parts;
    }

    private static IReadOnlyList<TextMergePart> BuildThreeWay(
        IReadOnlyList<string> baseLines,
        IReadOnlyList<string> personalLines,
        IReadOnlyList<string> companyLines)
    {
        var personalEdits = CreateEdits(baseLines, personalLines);
        var companyEdits = CreateEdits(baseLines, companyLines);
        if (personalEdits.Count == 0 && companyEdits.Count == 0)
        {
            return [CreatePart(0, TextMergePartKind.Unchanged, baseLines, personalLines, companyLines, baseLines)];
        }

        var tagged = personalEdits.Select(edit => new TaggedEdit(MergeSide.Personal, edit))
            .Concat(companyEdits.Select(edit => new TaggedEdit(MergeSide.Company, edit)))
            .OrderBy(item => item.Edit.Start)
            .ThenBy(item => item.Edit.End)
            .ThenBy(item => item.Side)
            .ToArray();
        var parts = new List<TextMergePart>();
        var cursor = 0;
        var index = 0;
        while (index < tagged.Length)
        {
            var cluster = new List<TaggedEdit> { tagged[index++] };
            var clusterStart = cluster[0].Edit.Start;
            var clusterEnd = cluster[0].Edit.End;
            while (index < tagged.Length && JoinsCluster(clusterStart, clusterEnd, tagged[index].Edit))
            {
                cluster.Add(tagged[index]);
                clusterEnd = Math.Max(clusterEnd, tagged[index].Edit.End);
                index++;
            }

            AddUnchanged(parts, baseLines, cursor, clusterStart);
            var personalCluster = cluster.Where(item => item.Side == MergeSide.Personal)
                .Select(item => item.Edit)
                .OrderBy(edit => edit.Start)
                .ToArray();
            var companyCluster = cluster.Where(item => item.Side == MergeSide.Company)
                .Select(item => item.Edit)
                .OrderBy(edit => edit.Start)
                .ToArray();
            var baseSlice = Slice(baseLines, clusterStart, clusterEnd);
            var personalResult = ApplyEdits(baseLines, clusterStart, clusterEnd, personalCluster);
            var companyResult = ApplyEdits(baseLines, clusterStart, clusterEnd, companyCluster);
            TextMergePartKind kind;
            IReadOnlyList<string> suggested;
            if (personalCluster.Length == 0)
            {
                kind = TextMergePartKind.AutoCompany;
                suggested = companyResult;
            }
            else if (companyCluster.Length == 0)
            {
                kind = TextMergePartKind.AutoPersonal;
                suggested = personalResult;
            }
            else if (personalResult.SequenceEqual(companyResult, StringComparer.Ordinal))
            {
                kind = TextMergePartKind.AutoSame;
                suggested = personalResult;
            }
            else
            {
                kind = TextMergePartKind.Conflict;
                suggested = personalResult;
            }

            parts.Add(CreatePart(
                parts.Count,
                kind,
                baseSlice,
                personalResult,
                companyResult,
                suggested));
            cursor = clusterEnd;
        }

        AddUnchanged(parts, baseLines, cursor, baseLines.Count);
        return parts;
    }

    private static IReadOnlyList<LineEdit> CreateEdits(
        IReadOnlyList<string> original,
        IReadOnlyList<string> modified)
    {
        if (original.SequenceEqual(modified, StringComparer.Ordinal))
        {
            return [];
        }

        if ((long)(original.Count + 1) * (modified.Count + 1) > MaximumLcsCells)
        {
            return [new LineEdit(0, original.Count, modified.ToArray())];
        }

        var lengths = new int[original.Count + 1, modified.Count + 1];
        for (var originalIndex = original.Count - 1; originalIndex >= 0; originalIndex--)
        {
            for (var modifiedIndex = modified.Count - 1; modifiedIndex >= 0; modifiedIndex--)
            {
                lengths[originalIndex, modifiedIndex] = string.Equals(
                    original[originalIndex],
                    modified[modifiedIndex],
                    StringComparison.Ordinal)
                    ? lengths[originalIndex + 1, modifiedIndex + 1] + 1
                    : Math.Max(
                        lengths[originalIndex + 1, modifiedIndex],
                        lengths[originalIndex, modifiedIndex + 1]);
            }
        }

        var result = new List<LineEdit>();
        var i = 0;
        var j = 0;
        var editStart = -1;
        var replacement = new List<string>();
        void Flush()
        {
            if (editStart < 0)
            {
                return;
            }

            result.Add(new LineEdit(editStart, i, replacement.ToArray()));
            editStart = -1;
            replacement.Clear();
        }

        while (i < original.Count && j < modified.Count)
        {
            if (string.Equals(original[i], modified[j], StringComparison.Ordinal))
            {
                Flush();
                i++;
                j++;
                continue;
            }

            editStart = editStart < 0 ? i : editStart;
            if (lengths[i + 1, j] >= lengths[i, j + 1])
            {
                i++;
            }
            else
            {
                replacement.Add(modified[j]);
                j++;
            }
        }

        if (i < original.Count || j < modified.Count)
        {
            editStart = editStart < 0 ? i : editStart;
            i = original.Count;
            while (j < modified.Count)
            {
                replacement.Add(modified[j++]);
            }
        }

        Flush();
        return result;
    }

    private static IReadOnlyList<string> ApplyEdits(
        IReadOnlyList<string> baseLines,
        int start,
        int end,
        IReadOnlyList<LineEdit> edits)
    {
        var result = new List<string>();
        var cursor = start;
        foreach (var edit in edits)
        {
            if (edit.Start < cursor || edit.End > end)
            {
                throw new InvalidDataException("三方合并编辑区间无效。");
            }

            result.AddRange(Slice(baseLines, cursor, edit.Start));
            result.AddRange(edit.Replacement);
            cursor = edit.End;
        }

        result.AddRange(Slice(baseLines, cursor, end));
        return result;
    }

    private static bool JoinsCluster(int clusterStart, int clusterEnd, LineEdit next)
    {
        if (next.Start < clusterEnd)
        {
            return true;
        }

        return clusterStart == clusterEnd && next.Start == clusterStart;
    }

    private static void AddUnchanged(
        ICollection<TextMergePart> parts,
        IReadOnlyList<string> source,
        int start,
        int end)
    {
        if (end <= start)
        {
            return;
        }

        var lines = Slice(source, start, end);
        parts.Add(CreatePart(parts.Count, TextMergePartKind.Unchanged, lines, lines, lines, lines));
    }

    private static TextMergePart CreatePart(
        int index,
        TextMergePartKind kind,
        IReadOnlyList<string> baseLines,
        IReadOnlyList<string> personalLines,
        IReadOnlyList<string> companyLines,
        IReadOnlyList<string> suggestedLines) =>
        new(
            index,
            kind,
            baseLines.ToArray(),
            personalLines.ToArray(),
            companyLines.ToArray(),
            suggestedLines.ToArray());

    private static IReadOnlyList<string> Slice(IReadOnlyList<string> source, int start, int end)
    {
        if (start < 0 || end < start || end > source.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(start), "文本差异区间无效。");
        }

        var result = new string[end - start];
        for (var index = start; index < end; index++)
        {
            result[index - start] = source[index];
        }

        return result;
    }

    private static IReadOnlyList<string> ToLines(bool exists, string text) =>
        exists ? TextFileCodec.NormalizeNewLines(text).Split('\n') : [];

    private static (bool Suggested, bool RequiresResolution, string Status) ResolveExistence(
        bool hasBase,
        bool baseExists,
        bool personalExists,
        bool companyExists)
    {
        if (personalExists == companyExists)
        {
            return (personalExists, false, personalExists ? "双方都保留文件" : "双方都删除文件");
        }

        if (!hasBase)
        {
            return (personalExists, true, "一侧文件不存在，需要明确保留或删除");
        }

        if (personalExists == baseExists)
        {
            return (companyExists, false, companyExists ? "工作空间新增文件" : "工作空间删除文件");
        }

        return (personalExists, false, personalExists ? "个人新增文件" : "个人删除文件");
    }

    private sealed record LineEdit(int Start, int End, IReadOnlyList<string> Replacement);

    private sealed record TaggedEdit(MergeSide Side, LineEdit Edit);

    private enum MergeSide
    {
        Personal,
        Company
    }
}
