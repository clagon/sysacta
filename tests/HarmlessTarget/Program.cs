using System.Runtime.InteropServices;

// Does nothing harmful: writes the arguments it received (one per line) to args.txt next to itself, so the tests can
// check that the approved fixed arguments arrive unchanged. With "--window close" it shows one plain window that closes
// on WM_CLOSE; with "--window refuse-close" the window ignores WM_CLOSE, like an app waiting on a save prompt. Exits after
// 60 seconds at the latest. Reads no input and starts no other process.
File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "args.txt"), args);
_ = new Timer(_ => Environment.Exit(0), null, TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
if (OperatingSystem.IsWindows() && args is ["--window", var mode])
{
    PlainWindow.Run(refuseClose: mode == "refuse-close");
}
else
{
    Thread.Sleep(Timeout.Infinite);
}

internal static class PlainWindow
{
    private const uint WmClose = 0x0010, WmDestroy = 0x0002;
    private static bool _refuseClose;
    private static readonly WndProc Proc = WindowProc;

    public static void Run(bool refuseClose)
    {
        _refuseClose = refuseClose;
        var instance = Marshal.GetHINSTANCE(typeof(PlainWindow).Module);
        var wc = new WndClassEx { Size = (uint)Marshal.SizeOf<WndClassEx>(), WndProc = Marshal.GetFunctionPointerForDelegate(Proc), Instance = instance, ClassName = "HarmlessTargetWindow" };
        _ = RegisterClassExW(ref wc);
        var window = CreateWindowExW(0, "HarmlessTargetWindow", "HarmlessTarget", 0x00CF0000 /* WS_OVERLAPPEDWINDOW */, 0, 0, 320, 200, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        _ = ShowWindow(window, 4 /* SW_SHOWNOACTIVATE */);
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            _ = TranslateMessage(ref msg);
            _ = DispatchMessageW(ref msg);
        }
    }

    private static IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmClose)
        {
            if (!_refuseClose)
            {
                _ = DestroyWindow(window);
            }

            return IntPtr.Zero;
        }

        if (message == WmDestroy)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }

        return DefWindowProcW(window, message, wParam, lParam);
    }

    private delegate IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size, Style;
        public IntPtr WndProc;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName, ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam, LParam;
        public uint Time;
        public int X, Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern ushort RegisterClassExW(ref WndClassEx wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetMessageW(out Msg msg, IntPtr window, uint min, uint max);

    [DllImport("user32.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr DispatchMessageW(ref Msg msg);

    [DllImport("user32.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr DefWindowProcW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void PostQuitMessage(int exitCode);
}
