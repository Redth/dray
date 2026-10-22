using System.Runtime.InteropServices;
using Dray.Core.Engine;
using Dray.Core.Model;
using Dray.Core.Shell;
using Dray.Core.Theme;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Svg;
using Microsoft.Maui.Storage;
using Microsoft.UI.Windowing;
using Microsoft.Win32;
using Xaml = Microsoft.UI.Xaml;

namespace Dray.WinUI;

/// <summary>
/// Dray in the notification area: the counterpart of <c>MacStatusItem</c>, and what lets the window
/// close without Dray stopping.
/// <para>
/// The menu is the macOS one: how many containers are running, the running ones with Open, Stop and
/// Restart one level in (so nothing stops by being clicked slightly wrong), then All containers and
/// Stacks. Windows adds what a tray app is expected to have: Open Dray as the default item, the two
/// choices about where the window goes, and Quit. Built from the same <see cref="EntityStore"/> the
/// UI reads when the menu opens, so it never polls.
/// </para>
/// <para>
/// A notification-area icon cannot carry text the way the macOS menu bar does, so the running count
/// is in the tooltip and the menu's first line.
/// </para>
/// <para>
/// The Windows App SDK has no tray API. This is <c>Shell_NotifyIcon</c> with a hidden window for its
/// messages, a native popup menu, and an icon drawn from the same sprite geometry as the sidebar.
/// </para>
/// </summary>
public sealed class WinTray : IDisposable
{
    /// <summary>Set while the app has a tray, for a second launch to bring the window back through.</summary>
    public static WinTray? Current { get; private set; }

    const int MostListed = 10;

    const string CloseToTrayKey = "dray.tray.closeToTray";
    const string MinimizeToTrayKey = "dray.tray.minimizeToTray";
    const string ToldKey = "dray.tray.toldStillRunning";

    readonly EngineManager _engine;
    readonly IShellState _shell;
    readonly IShellBridge _bridge;
    readonly IPlatformTheme _theme;
    readonly Xaml.Window _window;
    readonly AppWindow _appWindow;

    // The window procedure is called from native code; without this reference the delegate is
    // collected and the next message crashes the process.
    readonly Native.WndProc _proc;
    readonly nint _messageWindow;
    readonly uint _taskbarCreated;

    nint _icon;
    bool _quitting;
    bool _disposed;

    public WinTray(Xaml.Window window, EngineManager engine, IShellState shell, IShellBridge bridge, IPlatformTheme theme)
    {
        _window = window;
        _appWindow = window.AppWindow;
        _engine = engine;
        _shell = shell;
        _bridge = bridge;
        _theme = theme;

        _proc = WindowProc;
        _messageWindow = Native.CreateMessageWindow(_proc);

        // Sent to every top-level window when Explorer restarts. The notification area forgets
        // every icon then, and an app that does not listen is gone from it until relaunched.
        _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");

        Add();

        _appWindow.Closing += OnClosing;
        _appWindow.Changed += OnChanged;

        Current = this;
    }

    static bool CloseToTray
    {
        get => Preferences.Default.Get(CloseToTrayKey, true);
        set => Preferences.Default.Set(CloseToTrayKey, value);
    }

    static bool MinimizeToTray
    {
        get => Preferences.Default.Get(MinimizeToTrayKey, false);
        set => Preferences.Default.Set(MinimizeToTrayKey, value);
    }

    // ---------------------------------------------------------------- the window

    void OnClosing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        if (_quitting || !CloseToTray) return;

        e.Cancel = true;
        HideToTray();
    }

    void OnChanged(AppWindow sender, AppWindowChangedEventArgs e)
    {
        if (!MinimizeToTray || !sender.IsVisible) return;

        if (sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }) HideToTray();
    }

    void HideToTray()
    {
        _appWindow.Hide();

        // Once, the first time: a window that vanishes when closed otherwise reads as Dray having
        // quit, and the user goes looking for it in the Start menu.
        if (Preferences.Default.Get(ToldKey, false)) return;
        Preferences.Default.Set(ToldKey, true);

        _ = _bridge.NotifyAsync(
            "Dray is still running",
            "It's in the notification area, keeping track of your containers. Quit it from there.");
    }

    /// <summary>Bring the window back, from the tray or from a second launch. Any thread.</summary>
    public void ShowWindow() => _window.DispatcherQueue.TryEnqueue(() =>
    {
        if (_disposed) return;

        _appWindow.Show();
        if (_appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();

        _window.Activate();
        Native.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(_window));
    });

    void Open(string route)
    {
        ShowWindow();
        _shell.RequestNavigation(route);
    }

    void Quit()
    {
        _quitting = true;
        Dispose();
        Microsoft.Maui.Controls.Application.Current?.Quit();
    }

    // ---------------------------------------------------------------- the icon

    void Add()
    {
        RedrawIcon();
        var data = Data();
        Native.Shell_NotifyIcon(Native.NIM_ADD, ref data);

        // Version 4: the callback says which event happened and where, and a left click arrives as
        // one select rather than a down/up pair to interpret.
        data.uVersion = Native.NOTIFYICON_VERSION_4;
        Native.Shell_NotifyIcon(Native.NIM_SETVERSION, ref data);
    }

    void Update()
    {
        var data = Data();
        Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref data);
    }

    Native.NOTIFYICONDATAW Data()
    {
        var running = _engine.Store.Containers.Count(c => c.State == DockerState.Running);

        return new Native.NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<Native.NOTIFYICONDATAW>(),
            hWnd = _messageWindow,
            uID = 1,
            uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP,
            uCallbackMessage = Native.CallbackMessage,
            hIcon = _icon,
            szTip = Describe(running, _engine.Store.Containers.Count),
        };
    }

    void RedrawIcon()
    {
        var previous = _icon;
        _icon = RenderIcon();
        if (previous != 0) Native.DestroyIcon(previous);
    }

    /// <summary>
    /// The container glyph, drawn from the sprite at the notification area's size for this display,
    /// in the ink that reads on the taskbar. The taskbar has its own light or dark setting, apart
    /// from the apps' one, and the icon sits on the taskbar.
    /// </summary>
    static nint RenderIcon()
    {
        var size = Native.GetSystemMetricsForDpi(Native.SM_CXSMICON, Native.GetDpiForSystem());
        var ink = DrayTokens.Get(TaskbarIsLight() ? DrayTheme.Light : DrayTheme.Dark, DrayColor.Ink).ToHex();

        var device = CanvasDevice.GetSharedDevice();
        using var target = new CanvasRenderTarget(device, size, size, 96);
        using (var session = target.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Transparent);
            using var svg = CanvasSvgDocument.LoadFromXml(device, Icons.Svg(IconRef.Container, ink));
            session.DrawSvg(svg, new Windows.Foundation.Size(size, size));
        }

        return Native.CreateIcon(size, target.GetPixelBytes());
    }

    static bool TaskbarIsLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
    }

    static string Describe(int running, int total) => (running, total) switch
    {
        (0, 0) => "Dray — nothing here yet",
        (0, _) => $"Dray — nothing running, {total} stopped",
        (_, _) => $"Dray — {running} running of {total}",
    };

    // ---------------------------------------------------------------- messages and the menu

    nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == Native.CallbackMessage)
        {
            switch ((uint)(lParam & 0xFFFF))
            {
                case Native.NIN_SELECT:
                case Native.NIN_KEYSELECT:
                    ShowWindow();
                    break;

                case Native.WM_CONTEXTMENU:
                    ShowMenu((short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF));
                    break;

                case Native.WM_MOUSEMOVE:
                    // The tooltip is read from the icon's data, so it is brought up to date as the
                    // pointer arrives rather than on every engine event.
                    Update();
                    break;
            }

            return 0;
        }

        if (message == _taskbarCreated)
        {
            Add();
            return 0;
        }

        // The taskbar switched between light and dark: redraw the glyph in the ink that reads on it.
        if (message == Native.WM_SETTINGCHANGE && lParam != 0 && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
        {
            RedrawIcon();
            Update();
            return 0;
        }

        return Native.DefWindowProc(hwnd, message, wParam, lParam);
    }

    void ShowMenu(int x, int y)
    {
        var containers = _engine.Store.Containers;
        var running = containers.Where(c => c.State == DockerState.Running).ToList();
        var commands = new Dictionary<uint, Action>();
        uint next = 1;

        uint Command(Action action)
        {
            commands[next] = action;
            return next++;
        }

        // Destroying the menu destroys the submenus in it.
        var menu = Native.CreatePopupMenu();

        Native.AppendMenu(menu, Native.MF_STRING | Native.MF_GRAYED, 0, Describe(running.Count, containers.Count));
        Native.AppendMenu(menu, Native.MF_SEPARATOR, 0, null);

        foreach (var container in running.Take(MostListed))
        {
            var captured = container;
            var submenu = Native.CreatePopupMenu();

            Native.AppendMenu(submenu, Native.MF_STRING, Command(() => Open($"/containers/{captured.ShortId}")), "Open in Dray");
            Native.AppendMenu(submenu, Native.MF_STRING, Command(() => _ = _engine.PerformAsync(captured.Id, ContainerAction.Stop)), "Stop");
            Native.AppendMenu(submenu, Native.MF_STRING, Command(() => _ = _engine.PerformAsync(captured.Id, ContainerAction.Restart)), "Restart");

            // A popup item's id is the submenu's handle, at full pointer width.
            Native.AppendMenu(menu, Native.MF_POPUP, submenu, Escape(captured.Name));
        }

        if (running.Count > MostListed)
            Native.AppendMenu(menu, Native.MF_STRING | Native.MF_GRAYED, 0, $"and {running.Count - MostListed} more…");

        if (running.Count > 0) Native.AppendMenu(menu, Native.MF_SEPARATOR, 0, null);

        Native.AppendMenu(menu, Native.MF_STRING, Command(() => Open("/containers")), "All containers");
        Native.AppendMenu(menu, Native.MF_STRING, Command(() => Open("/stacks")), "Stacks");
        Native.AppendMenu(menu, Native.MF_SEPARATOR, 0, null);

        var open = Command(ShowWindow);
        Native.AppendMenu(menu, Native.MF_STRING, open, "Open Dray");
        Native.SetMenuDefaultItem(menu, open, 0);

        Native.AppendMenu(menu, Native.MF_STRING | (CloseToTray ? Native.MF_CHECKED : 0),
            Command(() => CloseToTray = !CloseToTray), "Keep running when the window is closed");
        Native.AppendMenu(menu, Native.MF_STRING | (MinimizeToTray ? Native.MF_CHECKED : 0),
            Command(() => MinimizeToTray = !MinimizeToTray), "Minimize to the notification area");

        Native.AppendMenu(menu, Native.MF_SEPARATOR, 0, null);
        Native.AppendMenu(menu, Native.MF_STRING, Command(Quit), "Quit Dray");

        // The menu in Dray's own light or dark. Without this a native popup menu is always light.
        Native.SetMenuTheme(_theme.Current == DrayTheme.Dark);

        // Required for the menu to close when the user clicks elsewhere, a long-standing rule of
        // notification-area menus.
        Native.SetForegroundWindow(_messageWindow);
        var chosen = Native.TrackPopupMenuEx(menu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON, x, y, _messageWindow, 0);
        Native.PostMessage(_messageWindow, Native.WM_NULL, 0, 0);

        Native.DestroyMenu(menu);

        if (commands.TryGetValue(chosen, out var action)) action();
    }

    /// <summary>An ampersand in a menu label marks the next letter as its access key.</summary>
    static string Escape(string label) => label.Replace("&", "&&", StringComparison.Ordinal);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _appWindow.Closing -= OnClosing;
        _appWindow.Changed -= OnChanged;

        var data = new Native.NOTIFYICONDATAW { cbSize = Marshal.SizeOf<Native.NOTIFYICONDATAW>(), hWnd = _messageWindow, uID = 1 };
        Native.Shell_NotifyIcon(Native.NIM_DELETE, ref data);

        if (_icon != 0) Native.DestroyIcon(_icon);
        Native.DestroyWindow(_messageWindow);

        if (Current == this) Current = null;
    }

    /// <summary>The Win32 the notification area is made of. Nothing here knows about Dray.</summary>
    static class Native
    {
        public const uint CallbackMessage = 0x8000 + 1; // WM_APP + 1

        public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
        public const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_SHOWTIP = 0x80;
        public const uint NOTIFYICON_VERSION_4 = 4;
        public const uint NIN_SELECT = 0x400, NIN_KEYSELECT = 0x401;

        public const uint WM_NULL = 0x0, WM_SETTINGCHANGE = 0x1A, WM_CONTEXTMENU = 0x7B, WM_MOUSEMOVE = 0x200;
        public const uint MF_STRING = 0x0, MF_GRAYED = 0x1, MF_CHECKED = 0x8, MF_POPUP = 0x10, MF_SEPARATOR = 0x800;
        public const uint TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100;
        public const int SM_CXSMICON = 49;

        public delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NOTIFYICONDATAW
        {
            public int cbSize;
            public nint hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public nint hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public nint hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEXW
        {
            public int cbSize;
            public uint style;
            public nint lpfnWndProc;
            public int cbClsExtra, cbWndExtra;
            public nint hInstance, hIcon, hCursor, hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public nint hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ICONINFO
        {
            public bool fIcon;
            public int xHotspot, yHotspot;
            public nint hbmMask, hbmColor;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        /// <summary>
        /// An ordinary hidden top-level window, not a message-only one: TaskbarCreated and the theme
        /// change are broadcasts, and message-only windows do not receive broadcasts.
        /// </summary>
        public static nint CreateMessageWindow(WndProc proc)
        {
            var instance = GetModuleHandle(null);
            var wc = new WNDCLASSEXW
            {
                cbSize = Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
                hInstance = instance,
                lpszClassName = "DrayNotificationArea",
            };
            RegisterClassEx(ref wc);
            return CreateWindowEx(0, wc.lpszClassName, "Dray", 0, 0, 0, 0, 0, 0, 0, instance, 0);
        }

        /// <summary>An icon from top-down premultiplied BGRA, which is what Win2D renders.</summary>
        public static nint CreateIcon(int size, byte[] premultiplied)
        {
            // Icons take straight alpha.
            var pixels = (byte[])premultiplied.Clone();
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var a = pixels[i + 3];
                if (a is 0 or 255) continue;
                pixels[i] = (byte)Math.Min(255, pixels[i] * 255 / a);
                pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / a);
                pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / a);
            }

            var header = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = size,
                biHeight = -size,
                biPlanes = 1,
                biBitCount = 32,
            };

            var color = CreateDIBSection(0, ref header, 0, out var bits, 0, 0);
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            var mask = CreateBitmap(size, size, 1, 1, 0);

            var info = new ICONINFO { fIcon = true, hbmMask = mask, hbmColor = color };
            var icon = CreateIconIndirect(ref info);

            DeleteObject(color);
            DeleteObject(mask);
            return icon;
        }

        /// <summary>
        /// Native popup menus follow the app's light or dark mode only once the process has opted
        /// in, and the only way to opt in is uxtheme's unnamed export 135, SetPreferredAppMode
        /// (2 forces dark, 3 forces light), with 136 to apply it to menus. Undocumented, but what
        /// every Win32 app with a dark menu uses. If it is missing, the menu stays light.
        /// </summary>
        public static void SetMenuTheme(bool dark)
        {
            try
            {
                SetPreferredAppMode(dark ? 2 : 3);
                FlushMenuThemes();
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATAW data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern uint RegisterWindowMessage(string name);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern ushort RegisterClassEx(ref WNDCLASSEXW wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern nint CreateWindowEx(int exStyle, string className, string name, int style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

        [DllImport("user32.dll")]
        public static extern bool DestroyWindow(nint hwnd);

        [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
        public static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(nint hwnd);

        [DllImport("user32.dll")]
        public static extern nint CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")]
        public static extern bool AppendMenu(nint menu, uint flags, nint id, string? text);

        public static bool AppendMenu(nint menu, uint flags, uint id, string? text) => AppendMenu(menu, flags, (nint)id, text);

        [DllImport("user32.dll")]
        public static extern bool SetMenuDefaultItem(nint menu, uint item, uint byPosition);

        [DllImport("user32.dll")]
        public static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint tpm);

        [DllImport("user32.dll")]
        public static extern bool DestroyMenu(nint menu);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(nint icon);

        [DllImport("user32.dll")]
        static extern nint CreateIconIndirect(ref ICONINFO info);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetricsForDpi(int index, uint dpi);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForSystem();

        [DllImport("gdi32.dll")]
        static extern nint CreateDIBSection(nint hdc, ref BITMAPINFOHEADER header, uint usage, out nint bits, nint section, uint offset);

        [DllImport("gdi32.dll")]
        static extern nint CreateBitmap(int width, int height, uint planes, uint bitCount, nint bits);

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(nint handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern nint GetModuleHandle(string? name);

        [DllImport("uxtheme.dll", EntryPoint = "#135")] // design-lint-ok: an export ordinal, not a colour.
        static extern int SetPreferredAppMode(int mode);

        [DllImport("uxtheme.dll", EntryPoint = "#136")] // design-lint-ok: an export ordinal, not a colour.
        static extern void FlushMenuThemes();
    }
}
