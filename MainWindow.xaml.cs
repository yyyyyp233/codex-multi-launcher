using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexChannelLauncher.Core;

namespace CodexChannelLauncher;

public sealed class ProfileCardViewModel
{
    public required string IdentityKey { get; init; }

    public string? ProfileId { get; init; }

    public required bool IsPersonal { get; init; }

    public required string Eyebrow { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    public required string StatusText { get; init; }

    public required string PrimaryLabel { get; init; }

    public required string PrimaryValue { get; init; }

    public required string SecondaryLabel { get; init; }

    public required string SecondaryValue { get; init; }

    public required string TertiaryLabel { get; init; }

    public required string TertiaryValue { get; init; }

    public required string ActionText { get; init; }

    public required string IconGlyph { get; init; }

    public required Brush AccentBrush { get; init; }

    public required Brush AccentSoftBrush { get; init; }

    public required Brush CardBorderBrush { get; init; }

    public required Brush StatusBrush { get; init; }

    public required Brush StatusBackgroundBrush { get; init; }

    public required Brush StatusBorderBrush { get; init; }

    public required bool CanLaunch { get; init; }

    public required bool RequiresConfiguration { get; init; }

    public Visibility SettingsVisibility => IsPersonal ? Visibility.Collapsed : Visibility.Visible;
}

public partial class MainWindow : Window
{
    private const string PersonalIdentity = "personal";

    private readonly ProfileCoordinator coordinator;
    private readonly string? previewOutput;
    private readonly DispatcherTimer statusTimer;
    private CancellationTokenSource? toastCancellation;
    private TaskCompletionSource<bool>? dialogCompletion;
    private bool isBusy;
    private string? busyIdentity;

    public MainWindow(ProfileCoordinator coordinator, string? previewOutput = null)
    {
        this.coordinator = coordinator;
        this.previewOutput = string.IsNullOrWhiteSpace(previewOutput) ? null : previewOutput;
        InitializeComponent();

        coordinator.ProgressChanged += Coordinator_ProgressChanged;
        statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        statusTimer.Tick += async (_, _) => await RefreshStatusAsync(false);
        Loaded += MainWindow_Loaded;
        Closed += (_, _) =>
        {
            statusTimer.Stop();
            toastCancellation?.Cancel();
            coordinator.ProgressChanged -= Coordinator_ProgressChanged;
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RootContent.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

        await RefreshStatusAsync(false);
        if (previewOutput is null)
        {
            try
            {
                if ((await Task.Run(coordinator.GetProfiles)).Count == 0)
                {
                    await OpenSetupWindowAsync(null);
                }
            }
            catch (Exception exception)
            {
                ShowToast(exception.Message, true);
            }
        }

        if (previewOutput is null && SystemParameters.ClientAreaAnimation)
        {
            StartLavaMotion();
        }

        statusTimer.Start();
        if (previewOutput is not null)
        {
            statusTimer.Stop();
            await Task.Delay(700);
            CapturePreview(previewOutput);
            Close();
        }
    }

    private void StartLavaMotion()
    {
        AnimateLava(LavaVioletShift, TranslateTransform.XProperty, -35, 130, 19);
        AnimateLava(LavaVioletShift, TranslateTransform.YProperty, -25, 105, 23);
        AnimateLava(LavaVioletScale, ScaleTransform.ScaleXProperty, 0.92, 1.18, 17);
        AnimateLava(LavaVioletScale, ScaleTransform.ScaleYProperty, 1.15, 0.88, 21);
        AnimateLava(LavaVioletRotate, RotateTransform.AngleProperty, -11, 20, 29);
        AnimateLava(LavaTealShift, TranslateTransform.XProperty, 70, -125, 27);
        AnimateLava(LavaTealShift, TranslateTransform.YProperty, -55, 100, 22);
        AnimateLava(LavaTealScale, ScaleTransform.ScaleXProperty, 1.12, 0.87, 24);
        AnimateLava(LavaTealScale, ScaleTransform.ScaleYProperty, 0.9, 1.2, 19);
        AnimateLava(LavaTealRotate, RotateTransform.AngleProperty, 12, -22, 31);
        AnimateLava(LavaRoseShift, TranslateTransform.XProperty, -115, 125, 23);
        AnimateLava(LavaRoseShift, TranslateTransform.YProperty, 65, -105, 26);
        AnimateLava(LavaRoseScale, ScaleTransform.ScaleXProperty, 0.88, 1.2, 20);
        AnimateLava(LavaRoseScale, ScaleTransform.ScaleYProperty, 1.16, 0.9, 24);
        AnimateLava(LavaRoseRotate, RotateTransform.AngleProperty, -16, 24, 33);
        AnimateLava(LavaBlueShift, TranslateTransform.XProperty, -45, 155, 29);
        AnimateLava(LavaBlueShift, TranslateTransform.YProperty, 35, -120, 25);
        AnimateLava(LavaBlueScale, ScaleTransform.ScaleXProperty, 1.14, 0.9, 22);
        AnimateLava(LavaBlueScale, ScaleTransform.ScaleYProperty, 0.9, 1.17, 27);
        AnimateLava(LavaBlueRotate, RotateTransform.AngleProperty, 8, -20, 35);
    }

    private static void AnimateLava(
        Animatable target,
        DependencyProperty property,
        double from,
        double to,
        double seconds)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Timeline.SetDesiredFrameRate(animation, 24);
        target.BeginAnimation(property, animation);
    }

    private void Window_SourceInitialized(object? sender, EventArgs e) => NativeAppearance.Apply(this);

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private async void ProfileLaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProfileCardViewModel card)
        {
            return;
        }

        if (!card.IsPersonal && card.RequiresConfiguration)
        {
            await OpenSetupWindowAsync(card.ProfileId);
            return;
        }

        await TryLaunchAsync(card);
    }

    private async Task TryLaunchAsync(ProfileCardViewModel card)
    {
        if (isBusy)
        {
            return;
        }

        LaunchOutcome? outcome = null;
        SetBusy(true, card.IdentityKey);
        try
        {
            outcome = card.IsPersonal
                ? await coordinator.LaunchAsync(ChannelKind.Personal, ParallelToggle.IsChecked == true)
                : await coordinator.LaunchAsync(
                    ChannelKind.Company,
                    ParallelToggle.IsChecked == true,
                    card.ProfileId);
            if (!outcome.BlockedByOtherChannel)
            {
                ShowToast(outcome.Message, false);
            }
        }
        catch (Exception exception)
        {
            ShowToast(exception.Message, true);
        }
        finally
        {
            SetBusy(false, null);
        }

        if (outcome is not null && !outcome.BlockedByOtherChannel)
        {
            await RefreshStatusAsync(false);
        }

        if (outcome?.BlockedByOtherChannel == true && await ShowParallelDialogAsync(card.Title))
        {
            ParallelToggle.IsChecked = true;
            await TryLaunchAsync(card);
        }
    }

    private async void ProfileSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (isBusy ||
            (sender as FrameworkElement)?.DataContext is not ProfileCardViewModel { ProfileId: { } profileId })
        {
            return;
        }

        statusTimer.Stop();
        try
        {
            var scopedCoordinator = coordinator.CreateProfileScope(profileId);
            var window = new ConfigurationCenterWindow(scopedCoordinator)
            {
                Owner = this
            };
            window.ShowDialog();
            await RefreshStatusAsync(false);
        }
        catch (Exception exception)
        {
            ShowToast(exception.Message, true);
        }
        finally
        {
            statusTimer.Start();
        }
    }

    private async void NewProfileButton_Click(object sender, RoutedEventArgs e) =>
        await OpenSetupWindowAsync(null);

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshStatusAsync(true);

    private async Task RefreshStatusAsync(bool notify)
    {
        if (isBusy)
        {
            return;
        }

        try
        {
            var status = await Task.Run(coordinator.GetStatus);
            RenderStatus(status);
            if (notify)
            {
                ShowToast("运行状态与隔离边界已刷新。", false);
            }
        }
        catch (Exception exception)
        {
            IsolationStateText.Text = "隔离状态读取失败";
            IsolationStateText.Foreground = (Brush)FindResource("WarningBrush");
            if (notify)
            {
                ShowToast(exception.Message, true);
            }
        }
    }

    private void RenderStatus(RuntimeStatus status)
    {
        var cards = new List<ProfileCardViewModel>
        {
            CreatePersonalCard(status)
        };
        cards.AddRange(status.ManagedProfiles.Select((profile, index) =>
            CreateManagedCard(profile, status.Package, index)));
        ProfileCards.ItemsSource = cards;

        PackageVersionText.Text = status.Package is null
            ? "APP NOT FOUND"
            : $"APP  {status.Package.PackageVersion}  ·  x64";
        PackageVersionText.ToolTip = status.Package?.ExecutablePath;

        var readyCount = status.ManagedProfiles.Count(profile =>
            profile.Metadata?.AuthConfigured == true && profile.Problem is null);
        var hasPackage = status.Package?.SupportsIsolatedElectronData == true;
        IsolationStateText.Text = status.ManagedProfiles.Count == 0
            ? "个人空间已就绪 · 尚未创建隔离空间"
            : hasPackage
                ? $"{readyCount}/{status.ManagedProfiles.Count} 个隔离空间已就绪"
                : "当前 App 不支持隔离启动入口";
        IsolationStateText.Foreground = hasPackage || status.ManagedProfiles.Count == 0
            ? (Brush)FindResource("MintBrush")
            : (Brush)FindResource("WarningBrush");
        IsolationStateText.ToolTip = status.Problem ?? $"运行数据：{coordinator.Paths.RuntimeRoot}";
    }

    private ProfileCardViewModel CreatePersonalCard(RuntimeStatus status)
    {
        var running = status.PersonalRunning;
        return new ProfileCardViewModel
        {
            IdentityKey = PersonalIdentity,
            IsPersonal = true,
            Eyebrow = "PERSONAL · PRIMARY",
            Title = "个人空间",
            Description = "主账号与默认 Codex Home；多开器不会改写这里的认证或配置。",
            StatusText = running ? $"运行中 · {status.PersonalRootProcessCount}" : "未运行",
            PrimaryLabel = "AUTHENTICATION",
            PrimaryValue = "ChatGPT Account",
            SecondaryLabel = "CODEX HOME",
            SecondaryValue = coordinator.Paths.PersonalCodexHome,
            TertiaryLabel = "ROLE",
            TertiaryValue = "默认主空间",
            ActionText = busyIdentity == PersonalIdentity ? "正在启动…" : "启动个人 Codex",
            IconGlyph = "P",
            AccentBrush = Brush("#75E2C1"),
            AccentSoftBrush = Brush("#193E37"),
            CardBorderBrush = Brush("#2A4C48"),
            StatusBrush = Brush(running ? "#86EBCB" : "#929AAF"),
            StatusBackgroundBrush = Brush(running ? "#142A25" : "#121722"),
            StatusBorderBrush = Brush(running ? "#285443" : "#2A3244"),
            CanLaunch = true,
            RequiresConfiguration = false
        };
    }

    private ProfileCardViewModel CreateManagedCard(
        ManagedProfileRuntimeStatus profile,
        CodexPackageInfo? package,
        int index)
    {
        var registration = profile.Registration;
        var metadata = profile.Metadata;
        var running = profile.Running;
        var authLabel = registration.AuthMode switch
        {
            ProfileAuthMode.ChatGptAccount => "ChatGPT Account",
            ProfileAuthMode.OpenAiApiKey => "OpenAI API Key",
            _ => "Responses Provider"
        };
        var endpoint = registration.AuthMode switch
        {
            ProfileAuthMode.ChatGptAccount => "在隔离 App 内登录",
            ProfileAuthMode.OpenAiApiKey => "api.openai.com/v1",
            _ => metadata?.BaseUrl.Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
                     .TrimEnd('/') ?? "配置不可用"
        };
        var color = registration.AccentColor;
        var requiresConfiguration = profile.Problem is not null || metadata?.AuthConfigured != true;
        var available = !requiresConfiguration && package?.SupportsIsolatedElectronData == true;
        return new ProfileCardViewModel
        {
            IdentityKey = registration.ProfileId,
            ProfileId = registration.ProfileId,
            IsPersonal = false,
            Eyebrow = $"ISOLATED · {authLabel.ToUpperInvariant()}",
            Title = registration.DisplayName,
            Description = "账号、配置、任务、插件与界面数据均存放在独立目录。",
            StatusText = running
                ? $"运行中 · {profile.RootProcessId}"
                : profile.Problem is not null
                    ? "需要修复"
                    : "未运行",
            PrimaryLabel = "AUTHENTICATION",
            PrimaryValue = authLabel,
            SecondaryLabel = "PROVIDER / MODEL",
            SecondaryValue = metadata is null
                ? "配置不可用"
                : $"{metadata.ProviderName} · {metadata.Model}",
            TertiaryLabel = "ENDPOINT",
            TertiaryValue = endpoint,
            ActionText = busyIdentity == registration.ProfileId
                ? "正在检查并启动…"
                : requiresConfiguration
                    ? "修复空间配置"
                    : package is null
                        ? "未找到 Codex App"
                        : package.SupportsIsolatedElectronData
                            ? $"启动 {registration.DisplayName}"
                            : "当前 App 不支持隔离启动",
            IconGlyph = (index + 1).ToString(),
            AccentBrush = Brush(color),
            AccentSoftBrush = Brush(WithAlpha(color, "28")),
            CardBorderBrush = Brush(WithAlpha(color, "66")),
            StatusBrush = Brush(running ? color : profile.Problem is not null ? "#F08D96" : "#929AAF"),
            StatusBackgroundBrush = Brush(running ? WithAlpha(color, "20") : "#121722"),
            StatusBorderBrush = Brush(running ? WithAlpha(color, "70") : "#2A3244"),
            CanLaunch = available,
            RequiresConfiguration = requiresConfiguration
        };
    }

    private void SetBusy(bool busy, string? identity)
    {
        isBusy = busy;
        busyIdentity = identity;
        ProfileCards.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        NewProfileButton.IsEnabled = !busy;
    }

    private void Coordinator_ProgressChanged(object? sender, LaunchProgress progress)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (isBusy)
            {
                IsolationStateText.Text = progress.Message;
            }
        });
    }

    private Task<bool> ShowParallelDialogAsync(string targetName)
    {
        dialogCompletion?.TrySetResult(false);
        dialogCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        DialogTitleText.Text = $"启动 {targetName} 需要并行模式";
        DialogBodyText.Text = "已有其他 Codex 实例正在运行。并行模式不会关闭或复用现有窗口；各隔离空间仍使用独立账号与数据目录。";
        DialogOverlay.Visibility = Visibility.Visible;
        DialogOverlay.Opacity = 0;
        DialogOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        return dialogCompletion.Task;
    }

    private void DialogCancelButton_Click(object sender, RoutedEventArgs e) => CompleteDialog(false);

    private void DialogConfirmButton_Click(object sender, RoutedEventArgs e) => CompleteDialog(true);

    private void CompleteDialog(bool result)
    {
        DialogOverlay.Visibility = Visibility.Collapsed;
        dialogCompletion?.TrySetResult(result);
        dialogCompletion = null;
    }

    private void ParallelToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (ParallelSummaryText is null)
        {
            return;
        }

        ParallelSummaryText.Text = ParallelToggle.IsChecked == true
            ? "本次已开启 · 允许多个独立空间同时运行"
            : "默认关闭 · 不自动关闭或复用其他实例";
        ParallelSummaryText.Foreground = Brush(ParallelToggle.IsChecked == true ? "#AEA3F5" : "#707B91");
    }

    private async Task<bool> OpenSetupWindowAsync(string? profileId)
    {
        if (isBusy)
        {
            return false;
        }

        statusTimer.Stop();
        try
        {
            var window = new WorkProfileSetupWindow(coordinator, profileId)
            {
                Owner = this
            };
            window.ShowDialog();
            await RefreshStatusAsync(false);
            return window.Completed;
        }
        finally
        {
            statusTimer.Start();
        }
    }

    private async void ShowToast(string message, bool error)
    {
        toastCancellation?.Cancel();
        toastCancellation?.Dispose();
        toastCancellation = new CancellationTokenSource();
        var token = toastCancellation.Token;
        ToastText.Text = message;
        ToastDot.Fill = Brush(error ? "#F08D96" : "#68DEB1");
        ToastHost.BorderBrush = Brush(error ? "#68404A" : "#3C465E");
        ToastHost.Visibility = Visibility.Visible;
        ToastHost.Opacity = 0;
        ToastHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));

        try
        {
            await Task.Delay(error ? 6200 : 3400, token);
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
            fade.Completed += (_, _) => ToastHost.Visibility = Visibility.Collapsed;
            ToastHost.BeginAnimation(OpacityProperty, fade);
        }
        catch (OperationCanceledException)
        {
            // Replaced by a newer message.
        }
    }

    private static SolidColorBrush Brush(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));

    private static string WithAlpha(string color, string alpha) =>
        color.Length == 7 ? $"#{alpha}{color[1..]}" : color;

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
}
