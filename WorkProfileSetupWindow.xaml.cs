using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexChannelLauncher.Core;
using Microsoft.Win32;

namespace CodexChannelLauncher;

public partial class WorkProfileSetupWindow : Window
{
    private readonly ProfileCoordinator coordinator;
    private readonly ProfileSetupStatus initialStatus;
    private readonly bool editingExisting;
    private bool isBusy;

    public WorkProfileSetupWindow(ProfileCoordinator coordinator)
    {
        this.coordinator = coordinator;
        initialStatus = coordinator.GetProfileSetupStatus();
        editingExisting = initialStatus.State == WorkProfileSetupState.Configured;

        InitializeComponent();
        PopulateInitialState();
    }

    public bool Completed { get; private set; }

    private void PopulateInitialState()
    {
        if (!string.IsNullOrWhiteSpace(initialStatus.Problem))
        {
            ProblemText.Text = initialStatus.Problem;
            ProblemPanel.Visibility = Visibility.Visible;
        }

        ExistingCandidateBox.ItemsSource = initialStatus.Candidates;
        ExistingCandidatePanel.Visibility = !editingExisting && initialStatus.Candidates.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (initialStatus.Candidates.Count > 0)
        {
            ExistingCandidateBox.SelectedIndex = 0;
        }

        if (!editingExisting)
        {
            UpdateModePanels();
            return;
        }

        var metadata = coordinator.GetStatus().CompanyProfile
                       ?? throw new InvalidDataException("工作空间元数据不可用。");
        HeadingText.Text = "编辑工作空间";
        SummaryText.Text = "修改仅作用于隔离工作空间；API Key 留空时保持原值。";
        ModeBox.IsEnabled = false;
        ModeBox.SelectedIndex = 0;
        DisplayNameBox.Text = metadata.DisplayName;
        ProviderIdBox.Text = metadata.Provider;
        ProviderNameBox.Text = metadata.ProviderName;
        BaseUrlBox.Text = metadata.BaseUrl;
        ModelBox.Text = metadata.Model;
        SelectReasoningEffort(metadata.ReasoningEffort);
        ApiKeyHintText.Text = "留空保留当前 API Key；输入新值时仅原子替换隔离 auth.json。";
        SaveButtonText.Text = "保存修改";
        UpdateModePanels();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e) => NativeAppearance.Apply(this);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ImportPanel is not null)
        {
            UpdateModePanels();
        }
    }

    private void UpdateModePanels()
    {
        var import = !editingExisting && SelectedMode() == ProfileSetupMode.Import;
        ImportPanel.Visibility = import ? Visibility.Visible : Visibility.Collapsed;
        ProviderPanel.Visibility = import ? Visibility.Collapsed : Visibility.Visible;
        SaveButtonText.Text = editingExisting
            ? "保存修改"
            : import
                ? "导入工作空间"
                : "创建工作空间";
    }

    private void BrowseImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择已有 Codex Home",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            ImportPathBox.Text = dialog.FolderName;
        }
    }

    private async void UseExistingButton_Click(object sender, RoutedEventArgs e)
    {
        if (ExistingCandidateBox.SelectedItem is not WorkProfileCandidate candidate)
        {
            SetFooter("请选择一个有效的旧工作空间。", true);
            return;
        }

        var request = new ProfileSetupRequest(
            ProfileSetupMode.RegisterExisting,
            DisplayNameBox.Text,
            ExistingProfileDirectoryName: candidate.ProfileDirectoryName);
        await SaveAsync(request);
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var mode = editingExisting ? ProfileSetupMode.Update : SelectedMode();
        var request = new ProfileSetupRequest(
            mode,
            DisplayNameBox.Text,
            ProviderIdBox.Text,
            ProviderNameBox.Text,
            BaseUrlBox.Text,
            ModelBox.Text,
            SelectedReasoningEffort(),
            ApiKeyBox.Password,
            ImportPathBox.Text);
        await SaveAsync(request);
    }

    private async Task SaveAsync(ProfileSetupRequest request)
    {
        if (isBusy)
        {
            return;
        }

        isBusy = true;
        SaveButton.IsEnabled = false;
        FooterText.Text = "正在安全写入配置…";
        try
        {
            await Task.Run(() => coordinator.ConfigureWorkProfile(request));
            ApiKeyBox.Clear();
            Completed = true;
            DialogResult = true;
        }
        catch (Exception exception)
        {
            ApiKeyBox.Clear();
            SetFooter(exception.Message, true);
        }
        finally
        {
            isBusy = false;
            SaveButton.IsEnabled = true;
        }
    }

    private ProfileSetupMode SelectedMode() =>
        (ModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Import"
            ? ProfileSetupMode.Import
            : ProfileSetupMode.Create;

    private string SelectedReasoningEffort() =>
        (ReasoningBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "high";

    private void SelectReasoningEffort(string value)
    {
        foreach (var item in ReasoningBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                ReasoningBox.SelectedItem = item;
                return;
            }
        }

        ReasoningBox.SelectedIndex = 3;
    }

    private void SetFooter(string message, bool error)
    {
        FooterText.Text = message;
        FooterText.Foreground = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(error ? "#F0ABB1" : "#7FE0BA"));
    }
}
