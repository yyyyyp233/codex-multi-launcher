using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CodexChannelLauncher.Core;

public static class NativeAppearance
{
    public static void Apply(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var source = HwndSource.FromHwnd(handle);
            if (source?.CompositionTarget is not null)
            {
                source.CompositionTarget.BackgroundColor = System.Windows.Media.Color.FromRgb(11, 14, 22);
            }

            var darkMode = 1;
            DwmSetWindowAttribute(handle, 20, ref darkMode, sizeof(int));

            var roundedCorners = 2;
            DwmSetWindowAttribute(handle, 33, ref roundedCorners, sizeof(int));

            var noSystemBorder = unchecked((int)0xFFFFFFFE);
            DwmSetWindowAttribute(handle, 34, ref noSystemBorder, sizeof(int));
        }
        catch
        {
            // The visual falls back to its own opaque gradient on older Windows builds.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
