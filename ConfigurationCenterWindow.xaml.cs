using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using CodexChannelLauncher.Core;

namespace CodexChannelLauncher;

public partial class ConfigurationCenterWindow : Window
{
    private readonly ProfileCoordinator coordinator;
    private readonly ConfigurationCenterService service;
    private readonly string? previewOutput;
    private bool isBusy;
    private bool isRendering;
    private bool companyRunning;
    private bool personalRunning;
    private bool chromeInstalled;
    private bool computerUseInstalled;
    private MemoryComparisonInfo? memoryComparison;

    public ConfigurationCenterWindow(ProfileCoordinator coordinator, string? previewOutput = null)
    {
        this.coordinator = coordinator;
        service = coordinator.ConfigurationCenter;
        this.previewOutput = string.IsNullOrWhiteSpace(previewOutput) ? null : previewOutput;
        InitializeComponent();
        ProfilePathText.Text = coordinator.Paths.CompanyCodexHome;
        ProfilePathText.ToolTip = coordinator.Paths.CompanyCodexHome;
        McpTypeBox.SelectedIndex = 0;
        Loaded += ConfigurationCenterWindow_Loaded;
    }

    private async void ConfigurationCenterWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RootContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        await RefreshAllAsync(false);

        if (previewOutput is not null)
        {
            await Task.Delay(450);
            CapturePreview(previewOutput);
            Close();
        }
    }

    private void Window_SourceInitialized(object? sender, EventArgs e) => NativeAppearance.Apply(this);

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private async void EditWorkProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (isBusy || companyRunning)
        {
            SetFooter("请先退出工作空间 App，再修改连接配置。", true);
            return;
        }

        var window = new WorkProfileSetupWindow(coordinator)
        {
            Owner = this
        };
        window.ShowDialog();
        if (window.Completed)
        {
            ProfilePathText.Text = coordinator.Paths.CompanyCodexHome;
            ProfilePathText.ToolTip = coordinator.Paths.CompanyCodexHome;
            await RefreshAllAsync(true);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAllAsync(true);

    private async Task<bool> RefreshAllAsync(bool notify, bool updateFooter = true)
    {
        if (isBusy)
        {
            return false;
        }

        SetBusy(true, "正在读取并计算配置差异…");
        try
        {
            var data = await Task.Run(() => new ConfigurationViewData(
                service.GetOverview(),
                service.GetSkills(),
                service.GetGlobalRules(),
                service.GetMemoryComparison(),
                service.GetCapabilities(),
                service.GetMcpComparisons(),
                service.GetPermissions(),
                service.GetSnapshots()));
            Render(data);
            if (updateFooter)
            {
                SetFooter(notify ? "配置差异已刷新。" : "配置中心已就绪。", false);
            }

            return true;
        }
        catch (Exception exception)
        {
            ProfileStateText.Text = "配置中心暂不可用";
            ProfileStateDot.Fill = Brush("#F08D96");
            SetFooter(exception.Message, true);
            return false;
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void Render(ConfigurationViewData data)
    {
        isRendering = true;
        try
        {
            companyRunning = data.Overview.CompanyRunning;
            personalRunning = data.Overview.PersonalRunning;
            memoryComparison = data.Memories;
            chromeInstalled = data.Capabilities.Chrome.Installed;
            computerUseInstalled = data.Capabilities.ComputerUse.Installed;
            ProfileStateText.Text = companyRunning && personalRunning
                ? "个人与工作空间 App 均在运行 · 双向合并前请退出两个 App"
                : companyRunning
                    ? "工作空间 App 正在运行 · 工作空间配置当前仅可查看"
                    : personalRunning
                        ? "个人 App 正在运行 · 工作空间配置可修改，双向合并需先退出个人 App"
                        : "双目录隔离已就绪 · 可以安全修改或双向合并";
            ProfileStateDot.Fill = Brush(companyRunning || personalRunning ? "#F3C777" : "#68DEB1");

            OverviewSkillCountText.Text = data.Skills.Count.ToString();
            OverviewMcpCountText.Text = data.Mcp.Count(item => item.CompanyExists).ToString();
            OverviewSnapshotCountText.Text = data.Snapshots.Count.ToString();
            OverviewChromeText.Text = FormatCapability(data.Capabilities.Chrome);
            OverviewComputerText.Text = FormatCapability(data.Capabilities.ComputerUse);
            OverviewPermissionText.Text =
                $"{data.Permissions.ApprovalPolicy} · {data.Permissions.SandboxMode} · {data.Permissions.WindowsSandbox}";
            OverviewMemoryText.Text = $"Memories · {data.Memories.CompanyFileCount} files · {data.Memories.StatusText}";
            FullAccessWarning.Visibility = data.Permissions.IsFullAccess ? Visibility.Visible : Visibility.Collapsed;

            var selectedSkill = (SkillList.SelectedItem as SkillComparisonItem)?.Name;
            SkillList.ItemsSource = data.Skills;
            SkillList.SelectedItem = data.Skills.FirstOrDefault(item =>
                item.Name.Equals(selectedSkill, StringComparison.OrdinalIgnoreCase));
            RenderSelectedSkill();

            var selectedGlobalRule = (GlobalRuleList.SelectedItem as GlobalRuleComparisonItem)?.FileName;
            GlobalRuleList.ItemsSource = data.GlobalRules;
            GlobalRuleList.SelectedItem = data.GlobalRules.FirstOrDefault(item =>
                                              item.FileName.Equals(
                                                  selectedGlobalRule,
                                                  StringComparison.OrdinalIgnoreCase)) ??
                                          data.GlobalRules.FirstOrDefault(item => item.State != ComparisonState.Same) ??
                                          data.GlobalRules.FirstOrDefault(item =>
                                              item.FileName.Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase));
            RenderSelectedGlobalRule();

            MemoryStatusText.Text = data.Memories.StatusText;
            MemorySizeText.Text =
                $"个人：{data.Memories.PersonalFileCount} files / {FormatBytes(data.Memories.PersonalBytes)} · " +
                $"工作空间：{data.Memories.CompanyFileCount} files / {FormatBytes(data.Memories.CompanyBytes)}";
            MemoryDiffText.Text = data.Memories.DiffText;
            RenderMemoryMergeButtons();
            MemoryFeatureToggle.IsChecked = data.Memories.FeatureEnabled;
            UseMemoriesToggle.IsChecked = data.Memories.UseMemories;
            GenerateMemoriesToggle.IsChecked = data.Memories.GenerateMemories;
            ExternalContextToggle.IsChecked = data.Memories.DisableOnExternalContext;

            ChromeInstallText.Text = data.Capabilities.Chrome.Detail;
            ComputerInstallText.Text = data.Capabilities.ComputerUse.Detail;
            ChromeToggle.IsChecked = data.Capabilities.Chrome.Enabled;
            ChromeToggle.IsEnabled = data.Capabilities.Chrome.Installed && !companyRunning;
            ComputerUseToggle.IsChecked = data.Capabilities.ComputerUse.Enabled;
            ComputerUseToggle.IsEnabled = data.Capabilities.ComputerUse.Installed && !companyRunning;
            AllowedAppsBox.Text = string.Join(Environment.NewLine, data.Capabilities.AllowedApps);
            AllowedAppsBox.IsReadOnly = companyRunning;
            SaveCapabilitiesButton.IsEnabled = !companyRunning;
            CapabilityEditNotice.Visibility = companyRunning ? Visibility.Visible : Visibility.Collapsed;
            ChromeSettingsButton.Content = chromeInstalled
                ? "打开工作空间 Chrome 设置"
                : "安装工作空间 Chrome 插件";
            ComputerSettingsButton.Content = computerUseInstalled
                ? "打开工作空间 Computer Use 插件页"
                : "安装工作空间 Computer Use 插件";

            var selectedMcp = (McpList.SelectedItem as McpComparisonItem)?.Name;
            McpList.ItemsSource = data.Mcp;
            McpList.SelectedItem = data.Mcp.FirstOrDefault(item =>
                item.Name.Equals(selectedMcp, StringComparison.OrdinalIgnoreCase));
            if (McpList.SelectedItem is null)
            {
                ClearMcpEditor();
            }
            else
            {
                RenderSelectedMcp();
            }

            RenderPermissions(data.Permissions);

            var selectedSnapshot = (SnapshotList.SelectedItem as SnapshotSummary)?.ArchivePath;
            SnapshotList.ItemsSource = data.Snapshots;
            SnapshotList.SelectedItem = data.Snapshots.FirstOrDefault(item =>
                item.ArchivePath.Equals(selectedSnapshot, StringComparison.OrdinalIgnoreCase));
            RenderSnapshotRestoreButton();
        }
        finally
        {
            isRendering = false;
        }
    }

    private void SkillList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RenderSelectedSkill();

    private void RenderSelectedSkill()
    {
        var selected = SkillList.SelectedItem as SkillComparisonItem;
        SelectedSkillText.Text = selected is null ? "尚未选择 Skill" : $"{selected.Name} · {selected.StatusText}";
        var canMerge = !isBusy && !companyRunning && !personalRunning && selected?.State != ComparisonState.Same;
        OpenSkillMergeWorkbenchButton.IsEnabled = selected?.State != ComparisonState.Same && !isBusy;
        ImportSkillButton.IsEnabled = selected?.PersonalExists == true && canMerge;
        ExportSkillButton.IsEnabled = selected?.CompanyExists == true && canMerge;
        ToggleSkillButton.IsEnabled = selected?.CompanyExists == true && !isBusy && !companyRunning;
        if (selected is not null)
        {
            ToggleSkillButton.Content = selected.CompanyEnabled ? "禁用工作空间 Skill" : "启用工作空间 Skill";
            SelectedSkillText.ToolTip = $"{selected.Name} · {selected.DiffText}";
        }
        else
        {
            SelectedSkillText.ToolTip = null;
        }
    }

    private async void ImportSkillButton_Click(object sender, RoutedEventArgs e)
    {
        if (SkillList.SelectedItem is not SkillComparisonItem selected)
        {
            return;
        }

        if (!Confirm(
                $"采用个人空间的 {selected.Name} 完整版本到工作空间？目标侧会先自动快照；个人与工作空间 App 都必须已退出。",
                "个人 → 工作空间"))
        {
            return;
        }

        await RunMutationAsync(
            "正在采用个人 Skill…",
            () => service.MergeSkillFromPersonal(selected.Name),
            $"{selected.Name} 已由个人版本更新到工作空间；下次启动生效。");
    }

    private async void OpenSkillMergeWorkbenchButton_Click(object sender, RoutedEventArgs e)
    {
        if (SkillList.SelectedItem is not SkillComparisonItem selected || isBusy)
        {
            return;
        }

        var window = new MergeWorkbenchWindow(
            coordinator.MergeWorkbench,
            MergeResourceKind.Skill,
            selected.Name)
        {
            Owner = this
        };
        window.ShowDialog();
        await RefreshAllAsync(false);
    }

    private async void ExportSkillButton_Click(object sender, RoutedEventArgs e)
    {
        if (SkillList.SelectedItem is not SkillComparisonItem selected ||
            !Confirm(
                $"采用工作空间的 {selected.Name} 完整版本到个人空间？这会修改个人 Skill，个人侧会先自动快照；两个 App 都必须已退出。",
                "工作空间 → 个人"))
        {
            return;
        }

        await RunMutationAsync(
            "正在采用工作空间 Skill…",
            () => service.MergeSkillFromCompany(selected.Name),
            $"{selected.Name} 已由工作空间版本更新到个人空间；下次启动生效。");
    }

    private async void ToggleSkillButton_Click(object sender, RoutedEventArgs e)
    {
        if (SkillList.SelectedItem is not SkillComparisonItem selected)
        {
            return;
        }

        await RunMutationAsync(
            "正在修改 Skill 状态…",
            () => service.SetSkillEnabled(selected.Name, !selected.CompanyEnabled),
            $"{selected.Name} 已{(selected.CompanyEnabled ? "禁用" : "启用")}；重启后生效。");
    }

    private void GlobalRuleList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RenderSelectedGlobalRule();

    private void RenderSelectedGlobalRule()
    {
        var selected = GlobalRuleList.SelectedItem as GlobalRuleComparisonItem;
        SelectedGlobalRuleText.Text = selected is null
            ? "尚未选择规则文件"
            : $"{selected.FileName} · {selected.StatusText}";
        var canMerge = !isBusy && !companyRunning && !personalRunning &&
                       selected?.State != ComparisonState.Same;
        OpenGlobalRuleMergeWorkbenchButton.IsEnabled =
            selected?.State != ComparisonState.Same && !isBusy;
        MergeGlobalRuleToCompanyButton.IsEnabled = selected?.PersonalExists == true && canMerge;
        MergeGlobalRuleToPersonalButton.IsEnabled = selected?.CompanyExists == true && canMerge;
        SelectedGlobalRuleText.ToolTip = selected is null
            ? null
            : $"{selected.PriorityText} · {selected.EffectiveText}";
    }

    private async void OpenGlobalRuleMergeWorkbenchButton_Click(object sender, RoutedEventArgs e)
    {
        if (GlobalRuleList.SelectedItem is not GlobalRuleComparisonItem selected || isBusy)
        {
            return;
        }

        var window = new MergeWorkbenchWindow(
            coordinator.MergeWorkbench,
            MergeResourceKind.GlobalRules,
            initialRelativePath: selected.FileName)
        {
            Owner = this
        };
        window.ShowDialog();
        await RefreshAllAsync(false);
    }

    private async void MergeGlobalRuleToCompanyButton_Click(object sender, RoutedEventArgs e)
    {
        if (GlobalRuleList.SelectedItem is not GlobalRuleComparisonItem selected ||
            !Confirm(
                $"采用个人空间的 {selected.FileName} 完整版本到工作空间？工作空间侧会先自动快照；两个 App 都必须已退出。",
                "个人 → 工作空间"))
        {
            return;
        }

        await RunMutationAsync(
            "正在采用个人全局规则…",
            () => service.MergeGlobalRuleFromPersonal(selected.FileName),
            $"{selected.FileName} 已由个人版本更新到工作空间；重新启动或新建任务后生效。");
    }

    private async void MergeGlobalRuleToPersonalButton_Click(object sender, RoutedEventArgs e)
    {
        if (GlobalRuleList.SelectedItem is not GlobalRuleComparisonItem selected ||
            !Confirm(
                $"采用工作空间的 {selected.FileName} 完整版本到个人空间？这会修改个人全局规则，个人侧会先自动快照；两个 App 都必须已退出。",
                "工作空间 → 个人"))
        {
            return;
        }

        await RunMutationAsync(
            "正在采用工作空间全局规则…",
            () => service.MergeGlobalRuleFromCompany(selected.FileName),
            $"{selected.FileName} 已由工作空间版本更新到个人空间；重新启动或新建任务后生效。");
    }

    private async void MergeMemoriesToCompanyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm(
                "把个人 Memories 合并到工作空间？同路径冲突采用个人版本，工作空间独有文件保留；工作空间侧会先自动快照，且两个 App 都必须已退出。",
                "个人 → 工作空间"))
        {
            return;
        }

        await RunMutationAsync(
            "正在合并 Memories 到工作空间…",
            service.MergeMemoriesFromPersonal,
            "个人 Memories 已合并到工作空间；下次启动使用。");
    }

    private async void MergeMemoriesToPersonalButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm(
                "把工作空间 Memories 合并到个人空间？同路径冲突采用工作空间版本，个人独有文件保留；个人侧会先自动快照，且两个 App 都必须已退出。",
                "工作空间 → 个人"))
        {
            return;
        }

        await RunMutationAsync(
            "正在合并 Memories 到个人空间…",
            service.MergeMemoriesFromCompany,
            "工作空间 Memories 已合并到个人空间；下次启动使用。");
    }

    private async void SaveMemorySettingsButton_Click(object sender, RoutedEventArgs e) =>
        await RunMutationAsync(
            "正在保存记忆策略…",
            () => service.ApplyMemorySettings(
                MemoryFeatureToggle.IsChecked == true,
                UseMemoriesToggle.IsChecked == true,
                GenerateMemoriesToggle.IsChecked == true,
                ExternalContextToggle.IsChecked == true),
            "记忆策略已保存；重启工作空间 App 后生效。");

    private async void SaveCapabilitiesButton_Click(object sender, RoutedEventArgs e)
    {
        var apps = AllowedAppsBox.Text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        await RunMutationAsync(
            "正在保存能力配置…",
            () => service.ApplyCapabilities(
                ChromeToggle.IsChecked == true,
                ComputerUseToggle.IsChecked == true,
                apps),
            "Chrome 与电脑操作配置已保存；重启工作空间 App 后生效。");
    }

    private async void OpenChromeSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!chromeInstalled)
        {
            var installed = await RunMutationAsync(
                "正在从当前 Codex App 安装 Chrome 插件…",
                () => service.InstallBundledPlugin("chrome"),
                "Chrome 官方内置插件已安装并启用。");
            if (!installed)
            {
                return;
            }
        }

        await OpenCompanyPageAsync("codex://settings/computer-use/google-chrome");
    }

    private async void OpenComputerSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!computerUseInstalled)
        {
            var installed = await RunMutationAsync(
                "正在从当前 Codex App 安装 Computer Use 插件…",
                () => service.InstallBundledPlugin("computer-use"),
                "Computer Use 官方内置插件已安装并启用。");
            if (!installed)
            {
                return;
            }
        }

        await OpenCompanyPageAsync("codex://plugins/computer-use@openai-bundled");
    }

    private async Task OpenCompanyPageAsync(string deepLink)
    {
        if (isBusy)
        {
            return;
        }

        SetBusy(true, "正在打开工作空间 Codex 设置页…");
        var opened = false;
        try
        {
            await coordinator.OpenCompanyDeepLinkAsync(deepLink);
            opened = true;
        }
        catch (Exception exception)
        {
            SetFooter(exception.Message, true);
        }
        finally
        {
            SetBusy(false, null);
        }

        if (opened && await RefreshAllAsync(false, false))
        {
            SetFooter("已在工作空间 Codex App 中打开对应设置页。需要修改配置时，请先退出工作空间 App 再刷新。", false);
        }
    }

    private void McpList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RenderSelectedMcp();

    private void RenderSelectedMcp()
    {
        if (McpList.SelectedItem is not McpComparisonItem selected)
        {
            ClearMcpEditor();
            return;
        }

        McpNameBox.Text = selected.Name;
        McpNameBox.IsReadOnly = true;
        SelectCombo(McpTypeBox, selected.Transport);
        McpAddressBox.Text = selected.Address;
        McpArgsBox.Text = string.Join(Environment.NewLine, selected.Arguments);
        McpEnabledToggle.IsChecked = selected.Enabled;
        ImportMcpButton.IsEnabled = selected.PersonalExists &&
                                    selected.State != ComparisonState.Same &&
                                    !selected.PersonalContainsSensitiveValues && !isBusy;
        RemoveMcpButton.IsEnabled = selected.CompanyExists && !isBusy;
        UpdateMcpEditorMode();
    }

    private void NewMcpButton_Click(object sender, RoutedEventArgs e)
    {
        McpList.SelectedItem = null;
        ClearMcpEditor();
        McpNameBox.Focus();
    }

    private void ClearMcpEditor()
    {
        McpNameBox.Text = string.Empty;
        McpNameBox.IsReadOnly = false;
        McpTypeBox.SelectedIndex = 0;
        McpAddressBox.Text = string.Empty;
        McpArgsBox.Text = string.Empty;
        McpEnabledToggle.IsChecked = true;
        ImportMcpButton.IsEnabled = false;
        RemoveMcpButton.IsEnabled = false;
        UpdateMcpEditorMode();
    }

    private void McpTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateMcpEditorMode();

    private void UpdateMcpEditorMode()
    {
        if (McpAddressLabel is null)
        {
            return;
        }

        var isHttp = ComboValue(McpTypeBox).Equals("HTTP", StringComparison.OrdinalIgnoreCase);
        McpAddressLabel.Text = isHttp ? "URL" : "命令";
        McpArgsBox.IsEnabled = !isHttp;
    }

    private async void ImportMcpButton_Click(object sender, RoutedEventArgs e)
    {
        if (McpList.SelectedItem is not McpComparisonItem selected)
        {
            return;
        }

        await RunMutationAsync(
            "正在迁移 MCP…",
            () => service.ImportMcpFromPersonal(selected.Name),
            $"MCP {selected.Name} 已迁移到工作空间 App；静态凭据未进入工作空间。");
    }

    private async void SaveMcpButton_Click(object sender, RoutedEventArgs e)
    {
        var name = McpNameBox.Text.Trim();
        var address = McpAddressBox.Text.Trim();
        if (name.Length == 0 || address.Length == 0)
        {
            SetFooter("MCP 名称和命令/URL 不能为空。", true);
            return;
        }

        var arguments = McpArgsBox.Text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        await RunMutationAsync(
            "正在保存 MCP…",
            () => service.SaveMcp(
                name,
                ComboValue(McpTypeBox),
                address,
                arguments,
                McpEnabledToggle.IsChecked == true),
            $"MCP {name} 已保存；重启工作空间 App 后连接。");
    }

    private async void RemoveMcpButton_Click(object sender, RoutedEventArgs e)
    {
        if (McpList.SelectedItem is not McpComparisonItem selected ||
            !Confirm($"确认从工作空间配置中删除 MCP {selected.Name}？个人配置不会变化。", "删除 MCP"))
        {
            return;
        }

        await RunMutationAsync(
            "正在删除 MCP…",
            () => service.RemoveMcp(selected.Name),
            $"MCP {selected.Name} 已从工作空间配置删除。");
    }

    private void PermissionPresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isRendering || PermissionPresetBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        switch (item.Tag?.ToString())
        {
            case "balanced":
                SetPermissionControls("on-request", "workspace-write", true, "unelevated");
                PermissionRiskText.Text = "适合日常开发：工作区可写，越界操作仍需审批。";
                PermissionRiskBox.Background = Brush("#18251F");
                PermissionRiskBox.BorderBrush = Brush("#315646");
                break;
            case "readonly":
                SetPermissionControls("on-request", "read-only", false, "unelevated");
                PermissionRiskText.Text = "只读审查：不写文件，默认关闭工具进程网络。";
                PermissionRiskBox.Background = Brush("#171D29");
                PermissionRiskBox.BorderBrush = Brush("#30384B");
                break;
            case "full":
                SetPermissionControls("never", "danger-full-access", true, "elevated");
                PermissionRiskText.Text = "高风险：无需审批、无文件沙箱并使用 elevated Windows sandbox。";
                PermissionRiskBox.Background = Brush("#31201E");
                PermissionRiskBox.BorderBrush = Brush("#6C443C");
                break;
        }
    }

    private void RenderPermissions(PermissionSettings settings)
    {
        SetPermissionControls(
            settings.ApprovalPolicy,
            settings.SandboxMode,
            settings.NetworkEnabled,
            settings.WindowsSandbox);
        var preset = settings.IsFullAccess
            ? "full"
            : settings.ApprovalPolicy == "on-request" && settings.SandboxMode == "workspace-write" &&
              settings.NetworkEnabled && settings.WindowsSandbox == "unelevated"
                ? "balanced"
                : settings.ApprovalPolicy == "on-request" && settings.SandboxMode == "read-only" &&
                  !settings.NetworkEnabled && settings.WindowsSandbox == "unelevated"
                    ? "readonly"
                    : "custom";
        SelectPreset(preset);
        PermissionRiskText.Text = settings.UsesNamedPermissionProfiles
            ? "检测到新版 permission profiles：启动器仅展示，不会与旧 sandbox_mode 混写。"
            : settings.IsFullAccess
                ? "当前为高风险 Full Access。每次修改仍会先创建启动器快照。"
                : "当前使用兼容的 sandbox_mode 权限配置。";
        PermissionRiskBox.Background = Brush(settings.IsFullAccess ? "#31201E" : "#181D29");
        PermissionRiskBox.BorderBrush = Brush(settings.IsFullAccess ? "#6C443C" : "#30384B");
    }

    private void SetPermissionControls(string approval, string sandbox, bool network, string windowsSandbox)
    {
        SelectCombo(ApprovalPolicyBox, approval);
        SelectCombo(SandboxModeBox, sandbox);
        SelectCombo(WindowsSandboxBox, windowsSandbox);
        NetworkToggle.IsChecked = network;
    }

    private async void SavePermissionsButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = new PermissionSettings(
            ComboValue(ApprovalPolicyBox),
            ComboValue(SandboxModeBox),
            NetworkToggle.IsChecked == true,
            ComboValue(WindowsSandboxBox),
            false);
        if (settings.IsFullAccess && !Confirm(
                "Full Access 会使用 never + danger-full-access，并允许 elevated Windows sandbox。确认仅对工作空间 App 应用？",
                "确认高风险权限"))
        {
            return;
        }

        await RunMutationAsync(
            "正在保存权限…",
            () => service.ApplyPermissions(settings),
            "工作空间 App 权限已保存；重启后生效。");
    }

    private void SnapshotList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RenderSnapshotRestoreButton();

    private async void CreateSnapshotButton_Click(object sender, RoutedEventArgs e) =>
        await RunMutationAsync(
            "正在创建快照…",
            service.CreateSnapshotNow,
            "工作空间配置快照已创建。");

    private async void RestoreSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotList.SelectedItem is not SnapshotSummary selected ||
            !Confirm(
                $"恢复 {selected.DisplayName}？恢复前会再自动保存当前{(selected.Target == "personal" ? "个人" : "工作空间")}状态，auth.json 不会被改动。",
                "恢复配置快照"))
        {
            return;
        }

        await RunMutationAsync(
            "正在校验并恢复快照…",
            () => service.RestoreSnapshot(selected.ArchivePath),
            $"{(selected.Target == "personal" ? "个人" : "工作空间")}快照已恢复；启动 App 前请核对配置状态。");
    }

    private void OpenCompanyFolderButton_Click(object sender, RoutedEventArgs e) => OpenFolder(service.CompanyHome);

    private void OpenSnapshotFolderButton_Click(object sender, RoutedEventArgs e) => OpenFolder(service.SnapshotDirectory);

    private async Task<bool> RunMutationAsync(string busyText, Func<SnapshotSummary> operation, string successText)
    {
        if (isBusy)
        {
            return false;
        }

        SetBusy(true, busyText);
        var succeeded = false;
        var finalMessage = string.Empty;
        var finalMessageIsError = false;
        try
        {
            var safetySnapshot = await Task.Run(operation);
            succeeded = true;
            var target = safetySnapshot.Target == "personal" ? "个人" : "工作空间";
            finalMessage = $"{successText} 变更前{target}快照：{safetySnapshot.CreatedAtUtc.ToLocalTime():HH:mm:ss}";
        }
        catch (Exception exception)
        {
            finalMessage = exception.Message;
            finalMessageIsError = true;
        }
        finally
        {
            SetBusy(false, null);
        }

        if (await RefreshAllAsync(false, false))
        {
            SetFooter(finalMessage, finalMessageIsError);
        }

        return succeeded;
    }

    private void SetBusy(bool busy, string? message)
    {
        isBusy = busy;
        RefreshButton.IsEnabled = !busy;
        ChromeToggle.IsEnabled = !busy && chromeInstalled && !companyRunning;
        ComputerUseToggle.IsEnabled = !busy && computerUseInstalled && !companyRunning;
        AllowedAppsBox.IsReadOnly = busy || companyRunning;
        SaveCapabilitiesButton.IsEnabled = !busy && !companyRunning;
        ChromeSettingsButton.IsEnabled = !busy;
        ComputerSettingsButton.IsEnabled = !busy;
        if (message is not null)
        {
            SetFooter(message, false);
        }

        RenderSelectedSkill();
        RenderSelectedGlobalRule();
        RenderMemoryMergeButtons();
        RenderSelectedMcp();
        RenderSnapshotRestoreButton();
    }

    private void RenderMemoryMergeButtons()
    {
        var canMerge = !isBusy && !companyRunning && !personalRunning &&
                       memoryComparison?.State != ComparisonState.Same;
        MergeMemoriesToCompanyButton.IsEnabled = canMerge && memoryComparison?.PersonalExists == true;
        MergeMemoriesToPersonalButton.IsEnabled = canMerge && memoryComparison?.CompanyExists == true;
    }

    private void RenderSnapshotRestoreButton()
    {
        if (SnapshotList.SelectedItem is not SnapshotSummary selected)
        {
            RestoreSnapshotButton.IsEnabled = false;
            return;
        }

        var targetRunning = selected.Target == "personal" ? personalRunning : companyRunning;
        RestoreSnapshotButton.IsEnabled = !isBusy && !targetRunning;
    }

    private void SetFooter(string message, bool error)
    {
        FooterStatusText.Text = message;
        FooterStatusText.Foreground = Brush(error ? "#F3A1AA" : "#A7B0C1");
        FooterStateDot.Fill = Brush(error ? "#F08D96" : "#68DEB1");
        FooterStatusText.ToolTip = message;
    }

    private static string FormatCapability(CapabilityStatus status) =>
        $"{status.DisplayName} · {(status.Installed ? status.Enabled ? "已启用" : "已安装 / 未启用" : "未安装")}";

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024d / 1024d:F1} MB",
        >= 1024 => $"{bytes / 1024d:F1} KB",
        _ => $"{bytes} B"
    };

    private static string ComboValue(ComboBox box) =>
        (box.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? box.Text;

    private static void SelectCombo(ComboBox box, string value)
    {
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
            string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase));
    }

    private void SelectPreset(string tag)
    {
        PermissionPresetBox.SelectedItem = PermissionPresetBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase));
    }

    private static SolidColorBrush Brush(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));

    private bool Confirm(string message, string title) =>
        MessageBox.Show(this, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var startInfo = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(path);
            Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            SetFooter(exception.Message, true);
        }
    }

    private void CapturePreview(string outputPath)
    {
        UpdateLayout();
        var visual = (FrameworkElement)Content;
        var width = Math.Max(1, (int)Math.Ceiling(visual.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(visual.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    private sealed record ConfigurationViewData(
        ConfigurationCenterOverview Overview,
        IReadOnlyList<SkillComparisonItem> Skills,
        IReadOnlyList<GlobalRuleComparisonItem> GlobalRules,
        MemoryComparisonInfo Memories,
        (CapabilityStatus Chrome, CapabilityStatus ComputerUse, IReadOnlyList<string> AllowedApps) Capabilities,
        IReadOnlyList<McpComparisonItem> Mcp,
        PermissionSettings Permissions,
        IReadOnlyList<SnapshotSummary> Snapshots);
}
