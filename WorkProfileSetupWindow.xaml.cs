using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexChannelLauncher.Core;
using Microsoft.Win32;

namespace CodexChannelLauncher;

public partial class WorkProfileSetupWindow : Window
{
    private readonly ProfileCoordinator coordinator;
    private readonly string? profileId;
    private readonly ProfileSetupStatus initialStatus;
    private readonly bool editingExisting;
    private readonly string? previewOutput;
    private bool isBusy;

    public WorkProfileSetupWindow(
        ProfileCoordinator coordinator,
        string? profileId = null,
        string? previewOutput = null)
    {
        this.coordinator = coordinator;
        this.profileId = profileId;
        this.previewOutput = string.IsNullOrWhiteSpace(previewOutput) ? null : previewOutput;
        initialStatus = string.IsNullOrWhiteSpace(profileId)
            ? new ProfileSetupStatus(WorkProfileSetupState.NotConfigured, null, null)
            : coordinator.GetProfileSetupStatus(profileId);
        editingExisting = !string.IsNullOrWhiteSpace(profileId) && initialStatus.Registration is not null;

        InitializeComponent();
        PopulateInitialState();
        if (this.previewOutput is not null)
        {
            Loaded += WorkProfileSetupWindow_Loaded;
        }
    }

    public bool Completed { get; private set; }

    private async void WorkProfileSetupWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await Task.Delay(220);
        WindowPreviewCapture.Save(this, previewOutput!);
        Close();
    }

    private void PopulateInitialState()
    {
        if (!string.IsNullOrWhiteSpace(initialStatus.Problem))
        {
            ProblemText.Text = initialStatus.Problem;
            ProblemPanel.Visibility = Visibility.Visible;
        }

        ModeBox.SelectedIndex = 0;
        AuthModeBox.SelectedIndex = 0;
        if (!editingExisting)
        {
            HeadingText.Text = "创建隔离空间";
            SummaryText.Text = "每个空间拥有独立的账号、配置、任务、插件与角标颜色。";
            UpdateModePanels();
            return;
        }

        CompanyProfileMetadata? metadata = null;
        try
        {
            metadata = coordinator.GetProfileMetadataForEditing(profileId!);
        }
        catch (Exception exception)
        {
            ProblemText.Text = initialStatus.Problem ?? exception.Message;
            ProblemPanel.Visibility = Visibility.Visible;
        }

        var registration = initialStatus.Registration!;
        HeadingText.Text = "编辑隔离空间";
        SummaryText.Text = "修改只作用于当前隔离空间；API Key 留空时保留原值。";
        ModeBox.IsEnabled = false;
        DisplayNameBox.Text = registration.DisplayName;
        SetSelectedAuthMode(registration.AuthMode);
        ProviderIdBox.Text = registration.AuthMode == ProfileAuthMode.CustomResponses && metadata is not null
            ? metadata.Provider
            : "custom";
        ProviderNameBox.Text = registration.AuthMode == ProfileAuthMode.CustomResponses && metadata is not null
            ? metadata.ProviderName
            : "Custom Provider";
        BaseUrlBox.Text = registration.AuthMode == ProfileAuthMode.CustomResponses && metadata is not null
            ? metadata.BaseUrl
            : "https://api.example.invalid/v1";
        ModelBox.Text = registration.AuthMode == ProfileAuthMode.ChatGptAccount
            ? string.Empty
            : metadata?.Model ?? string.Empty;
        SelectReasoningEffort(metadata?.ReasoningEffort ?? "high");
        ApiKeyHintText.Text = "留空保留当前 API Key；切换认证方式时必须输入新 Key。";
        SaveButtonText.Text = "保存修改";
        UpdateModePanels();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e) => NativeAppearance.Apply(this);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AttachPanel is not null)
        {
            UpdateModePanels();
        }
    }

    private void AuthModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CustomProviderPanel is not null)
        {
            UpdateModePanels();
        }
    }

    private void UpdateModePanels()
    {
        var attach = !editingExisting && SelectedMode() == ProfileSetupMode.Attach;
        var authMode = SelectedAuthMode();
        AttachPanel.Visibility = attach ? Visibility.Visible : Visibility.Collapsed;
        AuthenticationPanel.Visibility = attach ? Visibility.Collapsed : Visibility.Visible;
        CustomProviderPanel.Visibility = !attach && authMode == ProfileAuthMode.CustomResponses
            ? Visibility.Visible
            : Visibility.Collapsed;
        ModelSettingsPanel.Visibility = !attach && authMode != ProfileAuthMode.ChatGptAccount
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApiKeyPanel.Visibility = !attach && authMode != ProfileAuthMode.ChatGptAccount
            ? Visibility.Visible
            : Visibility.Collapsed;

        ApiKeyHintText.Text = editingExisting
            ? "留空保留当前 API Key；切换认证方式时必须输入新 Key。"
            : authMode == ProfileAuthMode.OpenAiApiKey
                ? "仅写入当前空间的 auth.json，不会回显或进入快照。"
                : "第三方 Key 仅写入当前空间的 auth.json，不会进入日志或快照。";
        SaveButtonText.Text = editingExisting
            ? "保存修改"
            : attach
                ? "接入已有空间"
                : "创建隔离空间";
    }

    private void BrowseExistingButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择已有工作空间目录或 Codex Home",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            ExistingPathBox.Text = dialog.FolderName;
        }
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
            ExistingPathBox.Text,
            ProfileId: profileId,
            AuthMode: SelectedAuthMode());
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
        FooterText.Text = request.Mode == ProfileSetupMode.Attach
            ? "正在原地注册已有工作空间…"
            : "正在安全写入隔离配置…";
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
        (ModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Attach"
            ? ProfileSetupMode.Attach
            : ProfileSetupMode.Create;

    private ProfileAuthMode SelectedAuthMode() =>
        Enum.TryParse<ProfileAuthMode>(
            (AuthModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
            out var mode)
            ? mode
            : ProfileAuthMode.ChatGptAccount;

    private string SelectedReasoningEffort() =>
        (ReasoningBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "high";

    private void SetSelectedAuthMode(ProfileAuthMode value)
    {
        foreach (var item in AuthModeBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), value.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                AuthModeBox.SelectedItem = item;
                return;
            }
        }

        AuthModeBox.SelectedIndex = 0;
    }

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
