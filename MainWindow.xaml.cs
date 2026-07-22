using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexChannelLauncher.Core;

namespace CodexChannelLauncher;

public partial class MainWindow : Window
{
    private readonly ProfileCoordinator coordinator;
    private readonly string? previewOutput;
    private readonly DispatcherTimer statusTimer;
    private CancellationTokenSource? toastCancellation;
    private TaskCompletionSource<bool>? dialogCompletion;
    private bool isBusy;

    public MainWindow(ProfileCoordinator coordinator, string? previewOutput = null)
    {
        this.coordinator = coordinator;
        this.previewOutput = string.IsNullOrWhiteSpace(previewOutput) ? null : previewOutput;

        InitializeComponent();

        PersonalHomeText.Text = coordinator.Paths.PersonalCodexHome;
        PersonalHomeText.ToolTip = coordinator.Paths.PersonalCodexHome;
        CompanyModelText.ToolTip = coordinator.Paths.CompanyConfig;
        coordinator.ProgressChanged += Coordinator_ProgressChanged;

        statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        statusTimer.Tick += async (_, _) => await RefreshStatusAsync(false);

        Loaded += MainWindow_Loaded;
        Closed += (_, _) =>
        {
            statusTimer.Stop();
            toastCancellation?.Cancel();
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        RootContent.BeginAnimation(OpacityProperty, fade);

        await RefreshStatusAsync(false);
        if (previewOutput is null)
        {
            var setup = await Task.Run(coordinator.GetProfileSetupStatus);
            if (setup.State != WorkProfileSetupState.Configured)
            {
                await OpenSetupWindowAsync();
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

    private async void LaunchPersonalButton_Click(object sender, RoutedEventArgs e) =>
        await TryLaunchAsync(ChannelKind.Personal);

    private async void LaunchCompanyButton_Click(object sender, RoutedEventArgs e)
    {
        var setup = await Task.Run(coordinator.GetProfileSetupStatus);
        if (setup.State != WorkProfileSetupState.Configured && !await OpenSetupWindowAsync())
        {
            return;
        }

        await TryLaunchAsync(ChannelKind.Company);
    }

    private async Task TryLaunchAsync(ChannelKind channel)
    {
        if (isBusy)
        {
            return;
        }

        LaunchOutcome? outcome = null;
        SetBusy(true, channel);
        try
        {
            outcome = await coordinator.LaunchAsync(channel, ParallelToggle.IsChecked == true);
            if (!outcome.BlockedByOtherChannel)
            {
                await RefreshStatusAsync(false);
                ShowToast(outcome.Message, false);
            }
        }
        catch (Exception exception)
        {
            ShowToast(exception.Message, true);
        }
        finally
        {
            SetBusy(false, channel);
        }

        if (outcome?.BlockedByOtherChannel == true)
        {
            var confirmed = await ShowParallelDialogAsync(channel);
            if (confirmed)
            {
                ParallelToggle.IsChecked = true;
                await TryLaunchAsync(channel);
            }
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshStatusAsync(true);
    }

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
                ShowToast("运行状态与配置边界已刷新。", false);
            }
        }
        catch (Exception exception)
        {
            IsolationStateText.Text = "隔离核对失败";
            IsolationStateText.Foreground = (Brush)FindResource("WarningBrush");
            if (notify)
            {
                ShowToast(exception.Message, true);
            }
        }
    }

    private void RenderStatus(RuntimeStatus status)
    {
        SetPill(
            PersonalStatusPill,
            PersonalStatusDot,
            PersonalStatusText,
            status.PersonalRunning,
            status.PersonalRunning
                ? $"运行中 · {status.PersonalRootProcessCount}"
                : "未运行");

        SetPill(
            CompanyStatusPill,
            CompanyStatusDot,
            CompanyStatusText,
            status.CompanyRunning,
            status.CompanyRunning
                ? $"运行中 · {status.CompanyRootProcessId}"
                : status.ProfileSetup.State switch
                {
                    WorkProfileSetupState.NotConfigured => "未配置",
                    WorkProfileSetupState.Invalid => "配置损坏",
                    _ => "未运行"
                });

        PackageVersionText.Text = status.Package is null
            ? "not found"
            : $"{status.Package.PackageVersion}  ·  x64";
        PackageVersionText.ToolTip = status.Package?.ExecutablePath;

        if (status.CompanyProfile is not null)
        {
            WorkProfileTitleText.Text = status.CompanyProfile.DisplayName;
            CompanyModelText.Text = $"{status.CompanyProfile.Provider} · {status.CompanyProfile.Model}";
            CompanyEndpointText.Text = status.CompanyProfile.BaseUrl
                .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
                .TrimEnd('/');
        }
        else
        {
            WorkProfileTitleText.Text = status.ProfileSetup.Registration?.DisplayName ?? "工作空间";
            CompanyModelText.Text = status.ProfileSetup.State == WorkProfileSetupState.Invalid
                ? "配置损坏"
                : "尚未配置";
            CompanyEndpointText.Text = status.ProfileSetup.Candidates.Count > 1
                ? $"发现 {status.ProfileSetup.Candidates.Count} 个旧空间"
                : "等待首次配置";
        }

        CompanyModelText.ToolTip = status.ProfileSetup.State == WorkProfileSetupState.Configured
            ? coordinator.Paths.CompanyConfig
            : status.ProfileSetup.Problem;
        WorkProfileEyebrowText.Text = status.ProfileSetup.State == WorkProfileSetupState.Configured
            ? "ISOLATED PROFILE"
            : "SETUP REQUIRED";

        var isolated = status.ProfileSetup.State == WorkProfileSetupState.Configured &&
                       status.Package?.SupportsIsolatedElectronData == true &&
                       status.CompanyProfile?.AuthConfigured == true;
        IsolationStateText.Text = isolated
            ? "双目录隔离已就绪"
            : "需要处理配置问题";
        IsolationStateText.Foreground = isolated
            ? (Brush)FindResource("MintBrush")
            : (Brush)FindResource("WarningBrush");
        IsolationStateText.ToolTip = status.Problem ??
                                     $"工作空间运行数据：{coordinator.Paths.RuntimeRoot}";

        var configured = status.ProfileSetup.State == WorkProfileSetupState.Configured;
        LaunchCompanyButtonText.Text = configured ? "启动工作空间 Codex" : "配置工作空间";
        LaunchCompanyButton.IsEnabled = !isBusy &&
                                        (!configured ||
                                         status.Package?.SupportsIsolatedElectronData == true &&
                                         status.CompanyProfile?.AuthConfigured == true);
    }

    private static void SetPill(
        System.Windows.Controls.Border pill,
        System.Windows.Shapes.Ellipse dot,
        System.Windows.Controls.TextBlock label,
        bool running,
        string text)
    {
        dot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(running ? "#68DEB1" : "#6F788D"));
        label.Text = text;
        label.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(running ? "#CFF8E8" : "#AAB2C3"));
        pill.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(running ? "#14251F" : "#121722"));
        pill.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(running ? "#285443" : "#2A3244"));
    }

    private void SetBusy(bool busy, ChannelKind channel)
    {
        isBusy = busy;
        LaunchPersonalButton.IsEnabled = !busy;
        LaunchCompanyButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;

        LaunchPersonalButtonText.Text = busy && channel == ChannelKind.Personal
            ? "正在建立个人边界…"
            : "启动个人 Codex";
        LaunchCompanyButtonText.Text = busy && channel == ChannelKind.Company
            ? "正在检查并启动…"
            : "启动工作空间 Codex";
    }

    private void Coordinator_ProgressChanged(object? sender, LaunchProgress progress)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (isBusy)
            {
                LaunchCompanyButtonText.Text = progress.Message;
            }
        });
    }

    private Task<bool> ShowParallelDialogAsync(ChannelKind channel)
    {
        dialogCompletion?.TrySetResult(false);
        dialogCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        DialogTitleText.Text = channel == ChannelKind.Company
            ? "个人 Codex 正在运行"
            : "工作空间 Codex 正在运行";
        DialogBodyText.Text = "当前为安全模式，因此多开器不会关闭现有窗口，也不会把新请求交给另一实例。你可以保持现状，或仅在本次会话中明确允许并行。";
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
            ? "本次已开启 · 账号与状态隔离，外部工作资源仍共享"
            : "默认关闭 · 两个实例不会被自动关闭或互相复用";
        ParallelSummaryText.Foreground = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(ParallelToggle.IsChecked == true ? "#AEA3F5" : "#707B91"));
    }

    private async void OpenConfigurationCenterButton_Click(object sender, RoutedEventArgs e)
    {
        if (isBusy)
        {
            return;
        }

        statusTimer.Stop();
        try
        {
            var setup = await Task.Run(coordinator.GetProfileSetupStatus);
            if (setup.State != WorkProfileSetupState.Configured && !await OpenSetupWindowAsync())
            {
                return;
            }

            var window = new ConfigurationCenterWindow(coordinator)
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

    private async Task<bool> OpenSetupWindowAsync()
    {
        if (isBusy)
        {
            return false;
        }

        var window = new WorkProfileSetupWindow(coordinator)
        {
            Owner = this
        };
        window.ShowDialog();
        await RefreshStatusAsync(false);
        return window.Completed;
    }

    private async void ShowToast(string message, bool error)
    {
        toastCancellation?.Cancel();
        toastCancellation?.Dispose();
        toastCancellation = new CancellationTokenSource();
        var token = toastCancellation.Token;

        ToastText.Text = message;
        ToastDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(error ? "#F08D96" : "#68DEB1"));
        ToastHost.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(error ? "#68404A" : "#3C465E"));
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
