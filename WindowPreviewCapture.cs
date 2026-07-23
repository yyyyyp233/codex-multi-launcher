using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexChannelLauncher;

internal static class WindowPreviewCapture
{
    public static void Save(Window window, string outputPath)
    {
        window.UpdateLayout();
        if (window.Content is not FrameworkElement visual)
        {
            throw new InvalidOperationException("窗口没有可渲染的内容。");
        }

        var width = Math.Max(1, (int)Math.Ceiling(visual.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(visual.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }
}
