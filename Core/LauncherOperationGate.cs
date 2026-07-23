using System.Diagnostics;

namespace CodexChannelLauncher.Core;

internal static class LauncherOperationGate
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    public static IDisposable Acquire(LauncherPaths paths, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Directory.CreateDirectory(paths.StateDirectory);

        var stopwatch = Stopwatch.StartNew();
        var effectiveTimeout = timeout ?? DefaultTimeout;
        IOException? lastFailure = null;
        do
        {
            try
            {
                return OpenExclusive(paths.OperationLockFile);
            }
            catch (IOException exception)
            {
                lastFailure = exception;
            }

            Thread.Sleep(RetryDelay);
        }
        while (stopwatch.Elapsed < effectiveTimeout);

        throw new TimeoutException(
            "另一个多开器窗口正在执行实例操作，请稍后重试。",
            lastFailure);
    }

    public static async Task<IDisposable> AcquireAsync(
        LauncherPaths paths,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Directory.CreateDirectory(paths.StateDirectory);

        var stopwatch = Stopwatch.StartNew();
        var effectiveTimeout = timeout ?? DefaultTimeout;
        IOException? lastFailure = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return OpenExclusive(paths.OperationLockFile);
            }
            catch (IOException exception)
            {
                lastFailure = exception;
            }

            await Task.Delay(RetryDelay, cancellationToken);
        }
        while (stopwatch.Elapsed < effectiveTimeout);

        throw new TimeoutException(
            "另一个多开器窗口正在执行实例操作，请稍后重试。",
            lastFailure);
    }

    private static FileStream OpenExclusive(string lockFile) =>
        new(
            lockFile,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.None);
}
