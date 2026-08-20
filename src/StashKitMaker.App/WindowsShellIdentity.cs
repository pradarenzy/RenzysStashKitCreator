using System.Runtime.InteropServices;
using System.IO;
using System.Windows;
using System.Windows.Interop;

namespace StashKitMaker.App;

internal static class WindowsShellIdentity
{
    private const string AppUserModelId = "Renzys.StashKitMaker";
    private const uint WmSetIcon = 0x0080;
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmDwmCompositionChanged = 0x031E;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private static System.Drawing.Icon? largeIcon;
    private static System.Drawing.Icon? smallIcon;

    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmSystemBackdropType = 38;

    public static void SetProcessIdentity()
    {
        _ = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
    }

    public static void ApplyWindowAppearance(Window window)
    {
        largeIcon ??= LoadEmbeddedIcon();
        if (largeIcon is null) return;
        smallIcon ??= new System.Drawing.Icon(largeIcon, new System.Drawing.Size(16, 16));
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        _ = SendMessage(handle, WmSetIcon, new IntPtr(1), largeIcon.Handle);
        _ = SendMessage(handle, WmSetIcon, IntPtr.Zero, smallIcon.Handle);
        var source = HwndSource.FromHwnd(handle);
        if (source?.CompositionTarget is not null)
            source.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
        source?.AddHook(WindowMessageHook);
        ApplyBackdrop(handle);
    }

    private static void ApplyBackdrop(IntPtr handle)
    {
        var lightMode = 0;
        var rounded = 2;
        _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref lightMode, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref rounded, sizeof(int));
        var frame = new Margins(-1, -1, -1, -1);
        _ = DwmExtendFrameIntoClientArea(handle, ref frame);
        if (Environment.OSVersion.Version.Build >= 22621)
        {
            // Acrylic is intentionally more visible than Mica while still respecting
            // the user's Windows transparency-effects preference.
            var acrylic = 3;
            _ = DwmSetWindowAttribute(handle, DwmSystemBackdropType, ref acrylic, sizeof(int));
            return;
        }
        ApplyAcrylicFallback(handle);
    }

    private static IntPtr WindowMessageHook(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmDwmCompositionChanged)
        {
            ApplyBackdrop(handle);
            return IntPtr.Zero;
        }
        if (message != WmGetMinMaxInfo || lParam == IntPtr.Zero) return IntPtr.Zero;
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;

        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return IntPtr.Zero;

        var minMax = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        minMax.MaxPosition.X = monitorInfo.WorkArea.Left - monitorInfo.MonitorArea.Left;
        minMax.MaxPosition.Y = monitorInfo.WorkArea.Top - monitorInfo.MonitorArea.Top;
        minMax.MaxSize.X = monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left;
        minMax.MaxSize.Y = monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top;
        Marshal.StructureToPtr(minMax, lParam, false);
        return IntPtr.Zero;
    }

    private static void ApplyAcrylicFallback(IntPtr handle)
    {
        var policy = new AccentPolicy { State = 3, Flags = 2, GradientColor = 0 };
        var size = Marshal.SizeOf<AccentPolicy>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, pointer, false);
            var data = new WindowCompositionAttributeData { Attribute = 19, Data = pointer, SizeOfData = size };
            _ = SetWindowCompositionAttribute(handle, ref data);
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private static System.Drawing.Icon? LoadEmbeddedIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/OkashiPlaceholder.ico", UriKind.Absolute));
        if (resource is not null)
        {
            using var source = new System.Drawing.Icon(resource.Stream);
            return (System.Drawing.Icon)source.Clone();
        }
        var executable = Environment.ProcessPath;
        return executable is not null && File.Exists(executable) ? System.Drawing.Icon.ExtractAssociatedIcon(executable) : null;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr window, ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Margins(int Left, int Right, int Top, int Bottom);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy { public int State; public int Flags; public int GradientColor; public int AnimationId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData { public int Attribute; public IntPtr Data; public int SizeOfData; }
}
