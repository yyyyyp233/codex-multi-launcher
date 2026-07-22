using System.Text;

namespace CodexChannelLauncher.Core;

public sealed class LauncherLog(LauncherPaths paths)
{
    private readonly object gate = new();

    public void Info(string message) => Write("INFO", message, null);

    public void Error(string message, Exception exception) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(paths.LogDirectory);
            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(' ')
                .Append(level)
                .Append(' ')
                .Append(message.ReplaceLineEndings(" "));

            if (exception is not null)
            {
                line.Append(" | ")
                    .Append(exception.GetType().Name)
                    .Append(": ")
                    .Append(exception.Message.ReplaceLineEndings(" "));
            }

            lock (gate)
            {
                File.AppendAllText(paths.LogFile, line.AppendLine().ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never weaken profile isolation or prevent launch.
        }
    }
}
