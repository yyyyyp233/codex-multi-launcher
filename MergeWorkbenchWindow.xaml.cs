using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexChannelLauncher.Core;

namespace CodexChannelLauncher;

public partial class MergeWorkbenchWindow : Window
{
    private readonly ProfileMergeService service;
    private readonly MergeResourceKind resourceKind;
    private readonly string containerName;
    private readonly string? initialRelativePath;
    private IReadOnlyList<MergeBlockViewModel> allBlocks = [];
    private MergeDocument? document;
    private MergeFileEntry? currentEntry;
    private bool isBusy;
    private bool isDirty;
    private bool suppressSelection;
    private bool companyRunning;
    private bool personalRunning;
    private bool resultExists;
    private bool existenceResolved;
    private bool hasResolution;
    private MergeResolutionKind resolution = MergeResolutionKind.Text;

    public MergeWorkbenchWindow(
        ProfileMergeService service,
        MergeResourceKind resourceKind,
        string containerName = "",
        string? initialRelativePath = null)
    {
        this.service = service;
        this.resourceKind = resourceKind;
        this.containerName = containerName;
        this.initialRelativePath = initialRelativePath;
        InitializeComponent();
        ScopeText.Text = resourceKind switch
        {
            MergeResourceKind.Skill => $"Skill · {containerName} · 逐块 Diff 与三方 Merge",
            MergeResourceKind.GlobalRules => "全局规则 · 逐块 Diff 与三方 Merge",
            _ => "Memories · 逐块 Diff 与三方 Merge"
        };
        Loaded += MergeWorkbenchWindow_Loaded;
    }

    private async void MergeWorkbenchWindow_Loaded(object sender, RoutedEventArgs e) =>
        await RefreshFileListAsync(initialRelativePath);

    private void Window_SourceInitialized(object? sender, EventArgs e) => NativeAppearance.Apply(this);

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!isDirty)
        {
            return;
        }

        if (!Confirm("当前文件有尚未保存的采纳结果，确认关闭并放弃？", "放弃合并结果"))
        {
            e.Cancel = true;
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (isDirty && !Confirm("刷新会放弃当前尚未保存的采纳结果，是否继续？", "刷新差异"))
        {
            return;
        }

        await RefreshFileListAsync(currentEntry?.RelativePath);
    }

    private async Task RefreshFileListAsync(string? preferredPath)
    {
        if (isBusy)
        {
            return;
        }

        SetBusy(true, "正在计算文件级差异…");
        try
        {
            var files = await Task.Run(() => service.GetFiles(resourceKind, containerName));
            suppressSelection = true;
            FileList.ItemsSource = files;
            var selected = files.FirstOrDefault(file =>
                file.RelativePath.Equals(preferredPath, StringComparison.OrdinalIgnoreCase));
            FileList.SelectedItem = selected;
            suppressSelection = false;
            FileCountText.Text = files.Count == 0
                ? "双方没有待处理差异"
                : $"{files.Count} 个待处理文件";
            isDirty = false;
            if (selected is null)
            {
                ClearDocument(files.Count == 0 ? "当前范围已经一致。" : "请选择差异文件。");
            }
            else
            {
                await LoadDocumentAsync(selected);
            }
        }
        catch (Exception exception)
        {
            SetFooter(exception.Message, true);
        }
        finally
        {
            suppressSelection = false;
            SetBusy(false, null);
            RefreshRuntimeState();
        }
    }

    private async void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressSelection || isBusy || FileList.SelectedItem is not MergeFileEntry selected ||
            ReferenceEquals(selected, currentEntry))
        {
            return;
        }

        if (isDirty && !Confirm("切换文件会放弃当前尚未保存的采纳结果，是否继续？", "切换差异文件"))
        {
            suppressSelection = true;
            FileList.SelectedItem = currentEntry;
            suppressSelection = false;
            return;
        }

        await LoadDocumentAsync(selected);
    }

    private async Task LoadDocumentAsync(MergeFileEntry entry)
    {
        SetBusy(true, "正在构建逐块差异…");
        try
        {
            var loaded = await Task.Run(() => service.LoadDocument(
                resourceKind,
                containerName,
                entry.RelativePath));
            document = loaded;
            currentEntry = entry;
            SelectedPathText.Text = loaded.RelativePath;
            SelectedPathText.ToolTip = loaded.RelativePath;
            DocumentMetaText.Text =
                $"个人：{FormatBytes(loaded.PersonalBytes)} / {loaded.PersonalEncoding} · " +
                $"工作空间：{FormatBytes(loaded.CompanyBytes)} / {loaded.CompanyEncoding}";
            WarningText.Text = loaded.WarningText;
            WarningBox.Visibility = string.IsNullOrWhiteSpace(loaded.WarningText)
                ? Visibility.Collapsed
                : Visibility.Visible;
            EmptySelectionText.Visibility = Visibility.Collapsed;
            AdoptPersonalFileButton.Content = loaded.PersonalExists
                ? "整文件采用个人"
                : "采用个人（删除）";
            AdoptCompanyFileButton.Content = loaded.CompanyExists
                ? "整文件采用工作空间"
                : "采用工作空间（删除）";

            if (loaded.IsText && loaded.TextPlan is not null)
            {
                RenderTextDocument(loaded.TextPlan);
            }
            else
            {
                RenderBinaryDocument(loaded);
            }

            isDirty = false;
            SetFooter("差异已加载；逐块采纳只修改内存中的结果，点击保存后才会写盘。", false);
        }
        catch (Exception exception)
        {
            ClearDocument(exception.Message);
            SetFooter(exception.Message, true);
        }
        finally
        {
            SetBusy(false, null);
            RefreshRuntimeState();
        }
    }

    private void RenderTextDocument(TextMergePlan plan)
    {
        TextMergePanel.Visibility = Visibility.Visible;
        BinaryPanel.Visibility = Visibility.Collapsed;
        resolution = MergeResolutionKind.Text;
        resultExists = plan.SuggestedExists;
        existenceResolved = !plan.ExistenceRequiresResolution;
        allBlocks = plan.Parts.Select(part => new MergeBlockViewModel(part, plan.HasBase, OnBlockChanged)).ToArray();
        var visibleBlocks = allBlocks.Where(block => block.IsChanged).ToArray();
        MergeBlockList.ItemsSource = visibleBlocks;
        hasResolution = visibleBlocks.Length > 0 ||
                        document!.PersonalFingerprint.Equals(
                            document.CompanyFingerprint,
                            StringComparison.OrdinalIgnoreCase);
        MergeSummaryText.Text = plan.HasBase
            ? $"三方模式 · 自动处理 {visibleBlocks.Count(block => block.IsResolved)} 块 · " +
              $"待解决 {visibleBlocks.Count(block => !block.IsResolved)} 块"
            : $"首次双向 Diff · {visibleBlocks.Length} 个差异块需确认";
        if (visibleBlocks.Length == 0)
        {
            MergeSummaryText.Text = "逐行文本一致；差异来自编码、BOM 或换行格式，请整文件采用一侧。";
        }

        UpdateResolutionState();
    }

    private void RenderBinaryDocument(MergeDocument loaded)
    {
        TextMergePanel.Visibility = Visibility.Collapsed;
        BinaryPanel.Visibility = Visibility.Visible;
        MergeBlockList.ItemsSource = null;
        allBlocks = [];
        resultExists = false;
        existenceResolved = false;
        hasResolution = false;
        BinaryDetailText.Text = string.IsNullOrWhiteSpace(loaded.WarningText)
            ? "二进制或超大文本不支持逐块编辑。"
            : loaded.WarningText;
        UpdateResolutionState();
    }

    private void AdoptPersonalFileButton_Click(object sender, RoutedEventArgs e) =>
        AdoptWholeFile(personal: true);

    private void AdoptCompanyFileButton_Click(object sender, RoutedEventArgs e) =>
        AdoptWholeFile(personal: false);

    private void AdoptWholeFile(bool personal)
    {
        if (document is null)
        {
            return;
        }

        var exists = personal ? document.PersonalExists : document.CompanyExists;
        if (!exists)
        {
            SelectDeleteResult();
            return;
        }

        resolution = personal ? MergeResolutionKind.PersonalFile : MergeResolutionKind.CompanyFile;
        resultExists = true;
        existenceResolved = true;
        hasResolution = true;
        if (document.IsText)
        {
            foreach (var block in allBlocks.Where(block => block.IsChanged))
            {
                if (personal)
                {
                    block.AdoptPersonal(notify: false);
                }
                else
                {
                    block.AdoptCompany(notify: false);
                }
            }
        }

        MarkDirty();
        SetFooter(personal ? "已在结果中采用个人整文件。" : "已在结果中采用工作空间整文件。", false);
        UpdateResolutionState();
    }

    private void KeepResultButton_Click(object sender, RoutedEventArgs e)
    {
        if (document is null || !document.IsText)
        {
            return;
        }

        resultExists = true;
        existenceResolved = true;
        resolution = MergeResolutionKind.Text;
        hasResolution = true;
        MarkDirty();
        UpdateResolutionState();
    }

    private void DeleteResultButton_Click(object sender, RoutedEventArgs e) => SelectDeleteResult();

    private void SelectDeleteResult()
    {
        if (document is null)
        {
            return;
        }

        resolution = MergeResolutionKind.Delete;
        resultExists = false;
        existenceResolved = true;
        hasResolution = true;
        MarkDirty();
        SetFooter("合并结果已设为删除目标文件；保存前不会写盘。", false);
        UpdateResolutionState();
    }

    private void AdoptPersonalBlockButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MergeBlockViewModel block)
        {
            block.AdoptPersonal();
        }
    }

    private void AdoptCompanyBlockButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MergeBlockViewModel block)
        {
            block.AdoptCompany();
        }
    }

    private void AdoptBothBlockButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MergeBlockViewModel block)
        {
            block.AdoptBoth();
        }
    }

    private void OnBlockChanged()
    {
        resolution = MergeResolutionKind.Text;
        resultExists = true;
        existenceResolved = true;
        hasResolution = true;
        MarkDirty();
        UpdateResolutionState();
    }

    private void TargetBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateResolutionState();

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (document is null || !CanSave())
        {
            return;
        }

        var target = SelectedTarget();
        var targetText = target switch
        {
            MergeWriteTarget.Personal => "个人空间",
            MergeWriteTarget.Company => "工作空间",
            MergeWriteTarget.Both => "个人与工作空间双方",
            _ => "目标空间"
        };
        var personalWarning = target is MergeWriteTarget.Personal or MergeWriteTarget.Both
            ? resourceKind == MergeResourceKind.GlobalRules
                ? " 此操作会明确修改个人全局规则。"
                : " 此操作会明确修改个人 Skills / Memories。"
            : string.Empty;
        if (!Confirm(
                $"确认把当前合并结果写入{targetText}？写入前会为每个目标建立快照。{personalWarning}",
                "保存逐块合并结果"))
        {
            return;
        }

        var resultText = resolution == MergeResolutionKind.Text
            ? ThreeWayTextMerge.JoinParts(allBlocks.OrderBy(block => block.Index)
                .Select(block => block.ResultLines))
            : string.Empty;
        var request = new MergeSaveRequest(
            document.ResourceKind,
            document.ContainerName,
            document.RelativePath,
            target,
            resolution,
            resultExists,
            resultText,
            document.PersonalFingerprint,
            document.CompanyFingerprint);
        var savedPath = document.RelativePath;
        string? successMessage = null;
        SetBusy(true, "正在创建目标快照并原子写入…");
        try
        {
            var result = await Task.Run(() => service.Save(request));
            isDirty = false;
            var snapshotTargets = string.Join("、", result.SafetySnapshots.Select(snapshot =>
                snapshot.Target == "personal" ? "个人" : "工作空间"));
            successMessage = $"{result.Detail} 已建立{snapshotTargets}快照。";
        }
        catch (Exception exception)
        {
            SetFooter(exception.Message, true);
        }
        finally
        {
            SetBusy(false, null);
            RefreshRuntimeState();
        }

        if (successMessage is not null)
        {
            await RefreshFileListAsync(savedPath);
            SetFooter(successMessage, false);
        }
    }

    private void RefreshRuntimeState()
    {
        companyRunning = service.IsCompanyRunning();
        personalRunning = service.IsPersonalRunning();
        if (!companyRunning && !personalRunning)
        {
            RuntimeText.Text = "个人与工作空间 App 均已退出 · 可以安全写回";
            RuntimeDot.Fill = Brush("#68DEB1");
        }
        else
        {
            var running = companyRunning && personalRunning
                ? "个人与工作空间 App"
                : companyRunning
                    ? "工作空间 App"
                    : "个人 App";
            RuntimeText.Text = $"{running}正在运行 · 当前仅可查看和编辑内存结果";
            RuntimeDot.Fill = Brush("#F3C777");
        }

        UpdateResolutionState();
    }

    private void UpdateResolutionState()
    {
        if (SaveButton is null || AdoptPersonalFileButton is null || TargetBox is null)
        {
            return;
        }

        AdoptPersonalFileButton.IsEnabled = document is not null && !isBusy;
        AdoptCompanyFileButton.IsEnabled = document is not null && !isBusy;
        KeepResultButton.IsEnabled = document?.IsText == true && !isBusy;
        DeleteResultButton.IsEnabled = document is not null && !isBusy;
        var unresolved = CurrentUnresolvedCount();
        ExistenceStateText.Text = resultExists
            ? existenceResolved ? "保留文件" : "保留 / 删除未决定"
            : existenceResolved ? "删除文件" : "保留 / 删除未决定";
        ExistenceStateText.Foreground = Brush(existenceResolved ? "#68DEB1" : "#F3C777");
        UnresolvedText.Text = document is null
            ? "—"
            : unresolved == 0
                ? "全部差异已解决"
                : $"仍有 {unresolved} 项未解决";
        SaveButton.IsEnabled = CanSave();
    }

    private int CurrentUnresolvedCount()
    {
        if (!hasResolution || document is null)
        {
            return 1;
        }

        if (resolution is MergeResolutionKind.PersonalFile or
            MergeResolutionKind.CompanyFile or
            MergeResolutionKind.Delete)
        {
            return 0;
        }

        return allBlocks.Count(block => block.IsChanged && !block.IsResolved) +
               (existenceResolved ? 0 : 1);
    }

    private bool CanSave() => document is not null && !isBusy && hasResolution &&
                              CurrentUnresolvedCount() == 0 &&
                              !companyRunning && !personalRunning;

    private MergeWriteTarget SelectedTarget()
    {
        var tag = (TargetBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return Enum.TryParse<MergeWriteTarget>(tag, true, out var target)
            ? target
            : MergeWriteTarget.Company;
    }

    private void MarkDirty() => isDirty = true;

    private void ClearDocument(string message)
    {
        document = null;
        currentEntry = null;
        allBlocks = [];
        MergeBlockList.ItemsSource = null;
        TextMergePanel.Visibility = Visibility.Collapsed;
        BinaryPanel.Visibility = Visibility.Collapsed;
        WarningBox.Visibility = Visibility.Collapsed;
        EmptySelectionText.Visibility = Visibility.Visible;
        EmptySelectionText.Text = message;
        SelectedPathText.Text = "请选择差异文件";
        DocumentMetaText.Text = "—";
        resultExists = false;
        existenceResolved = false;
        hasResolution = false;
        isDirty = false;
        UpdateResolutionState();
    }

    private void SetBusy(bool busy, string? message)
    {
        isBusy = busy;
        FileList.IsEnabled = !busy;
        TargetBox.IsEnabled = !busy;
        if (message is not null)
        {
            SetFooter(message, false);
        }

        UpdateResolutionState();
    }

    private void SetFooter(string message, bool error)
    {
        FooterStatusText.Text = message;
        FooterStatusText.Foreground = Brush(error ? "#F3A1AA" : "#A7B0C1");
        FooterStatusText.ToolTip = message;
    }

    private bool Confirm(string message, string title) =>
        MessageBox.Show(this, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) ==
        MessageBoxResult.OK;

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024d / 1024d:F1} MB",
        >= 1024 => $"{bytes / 1024d:F1} KB",
        _ => $"{bytes} B"
    };

    private static SolidColorBrush Brush(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));

    private sealed class MergeBlockViewModel : INotifyPropertyChanged
    {
        private readonly Action changed;
        private IReadOnlyList<string> resultLines;
        private string resolutionText;

        public MergeBlockViewModel(TextMergePart part, bool hasBase, Action changed)
        {
            this.changed = changed;
            Index = part.Index;
            IsChanged = part.IsChanged;
            IsResolved = !part.RequiresResolution;
            HeaderText = $"差异块 #{part.Index + 1} · {part.StatusText}";
            PersonalLines = part.PersonalLines;
            CompanyLines = part.CompanyLines;
            PersonalText = Display(part.PersonalLines, "∅（个人块无内容）");
            CompanyText = Display(part.CompanyLines, "∅（工作空间块无内容）");
            BaseText = hasBase
                ? Display(part.BaseLines, "∅（基线块无内容）")
                : "— 未记录共同基线 —";
            resultLines = part.SuggestedLines.ToArray();
            resolutionText = IsResolved ? part.StatusText : "未解决 · 当前仅预览个人块";
        }

        public int Index { get; }

        public bool IsChanged { get; }

        public bool IsResolved { get; private set; }

        public IReadOnlyList<string> PersonalLines { get; }

        public IReadOnlyList<string> CompanyLines { get; }

        public IReadOnlyList<string> ResultLines => resultLines;

        public string HeaderText { get; }

        public string PersonalText { get; }

        public string BaseText { get; }

        public string CompanyText { get; }

        public string ResolutionText
        {
            get => resolutionText;
            private set
            {
                resolutionText = value;
                OnPropertyChanged();
            }
        }

        public string ResultText
        {
            get => ThreeWayTextMerge.PartLinesToText(resultLines);
            set
            {
                var normalized = TextFileCodec.NormalizeNewLines(value ?? string.Empty);
                if (normalized.Equals(ThreeWayTextMerge.PartLinesToText(resultLines), StringComparison.Ordinal))
                {
                    return;
                }

                resultLines = ThreeWayTextMerge.TextToPartLines(normalized);
                IsResolved = true;
                ResolutionText = "已手工编辑";
                OnPropertyChanged();
                changed();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void AdoptPersonal(bool notify = true) =>
            SetResult(PersonalLines, "已采用个人块", notify);

        public void AdoptCompany(bool notify = true) =>
            SetResult(CompanyLines, "已采用工作空间块", notify);

        public void AdoptBoth(bool notify = true) =>
            SetResult(PersonalLines.Concat(CompanyLines).ToArray(), "已按个人 → 工作空间顺序保留两块", notify);

        private void SetResult(IReadOnlyList<string> lines, string status, bool notify)
        {
            resultLines = lines.ToArray();
            IsResolved = true;
            ResolutionText = status;
            OnPropertyChanged(nameof(ResultText));
            if (notify)
            {
                changed();
            }
        }

        private static string Display(IReadOnlyList<string> lines, string emptyText) =>
            lines.Count == 0 ? emptyText : ThreeWayTextMerge.PartLinesToText(lines);

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
