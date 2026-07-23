using System.Windows;
using CodexChannelLauncher.Core;

namespace CodexChannelLauncher;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        var diagnosticPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexChannelLauncher",
            "logs",
            "startup-error.log");

        try
        {
            EnsureWindowsDirectoryEnvironment();
            base.OnStartup(e);
            TryDeleteFile(diagnosticPath);

            var coordinator = new ProfileCoordinator();
            if (TryReadOutputArgument(e.Args, "--self-test", out var selfTestOutput))
            {
                var report = await Task.Run(coordinator.RunSelfTest);
                ProfileCoordinator.WriteReport(selfTestOutput, report);
                Shutdown(report.Passed ? 0 : 1);
                return;
            }

            if (TryReadOutputArgument(e.Args, "--smoke-launch-company", out var smokeOutput))
            {
                var report = await coordinator.RunCompanySmokeLaunchAsync();
                ProfileCoordinator.WriteReport(smokeOutput, report);
                Shutdown(report.Passed ? 0 : 1);
                return;
            }

            if (TryReadOutputArgument(e.Args, "--render-config-preview", out var configPreviewOutput))
            {
                var configWindow = new ConfigurationCenterWindow(coordinator, configPreviewOutput);
                MainWindow = configWindow;
                configWindow.Show();
                return;
            }

            if (TryReadOutputArgument(e.Args, "--render-merge-preview", out var mergePreviewOutput))
            {
                var mergeWindow = new MergeWorkbenchWindow(
                    coordinator.MergeWorkbench,
                    MergeResourceKind.GlobalRules,
                    previewOutput: mergePreviewOutput);
                MainWindow = mergeWindow;
                mergeWindow.Show();
                return;
            }

            if (TryReadOutputArgument(e.Args, "--render-setup-preview", out var setupPreviewOutput))
            {
                var setupWindow = new WorkProfileSetupWindow(coordinator, previewOutput: setupPreviewOutput);
                MainWindow = setupWindow;
                setupWindow.Show();
                return;
            }

            string? previewOutput = null;
            TryReadOutputArgument(e.Args, "--render-preview", out previewOutput);

            var window = new MainWindow(coordinator, previewOutput);
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(diagnosticPath)!);
                File.WriteAllText(diagnosticPath, exception.ToString());
            }
            catch
            {
                // Diagnostics must never hide the original startup failure.
            }

            if (e.Args.Any(value =>
                    value.Equals("--render-preview", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("--render-config-preview", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("--render-merge-preview", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("--render-setup-preview", StringComparison.OrdinalIgnoreCase)))
            {
                Shutdown(1);
                return;
            }

            MessageBox.Show(
                exception.GetBaseException().Message + $"\n\n诊断日志：{diagnosticPath}",
                "Codex 多开器",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // A stale diagnostic is harmless when another process still has it open.
        }
    }

    private static void EnsureWindowsDirectoryEnvironment()
    {
        var configuredWindowsDirectory = Environment.GetEnvironmentVariable("WINDIR");
        if (!string.IsNullOrWhiteSpace(configuredWindowsDirectory) &&
            Path.IsPathFullyQualified(configuredWindowsDirectory) &&
            Directory.Exists(Path.Combine(configuredWindowsDirectory, "Fonts")))
        {
            return;
        }

        var systemDirectory = Environment.SystemDirectory;
        var windowsDirectory = Directory.GetParent(systemDirectory)?.FullName;
        if (!string.IsNullOrWhiteSpace(windowsDirectory) &&
            Directory.Exists(Path.Combine(windowsDirectory, "Fonts")))
        {
            Environment.SetEnvironmentVariable("WINDIR", windowsDirectory, EnvironmentVariableTarget.Process);
        }
    }

    private static bool TryReadOutputArgument(string[] args, string name, out string output)
    {
        var index = Array.FindIndex(args, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < args.Length && !string.IsNullOrWhiteSpace(args[index + 1]))
        {
            output = Path.GetFullPath(args[index + 1]);
            return true;
        }

        output = string.Empty;
        return false;
    }

}
