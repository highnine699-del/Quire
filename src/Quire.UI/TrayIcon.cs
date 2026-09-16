using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Quire.UI;

/// <summary>
/// System-tray icon implemented directly via Shell_NotifyIcon Win32 API.
/// Zero NuGet dependencies — works on any Windows 10/11 machine without extra packages.
///
/// Provides:
///   - Left-click  → toggle widget visibility
///   - Right-click → context menu: Open/Hide · Open Settings · Quit
/// </summary>
public sealed class TrayIcon : IDisposable
{
    // ── Win32 constants ──────────────────────────────────────────────────────
    private const int WM_USER               = 0x0400;
    private const int WM_TRAYICON           = WM_USER + 1;
    private const int WM_LBUTTONUP          = 0x0202;
    private const int WM_RBUTTONUP          = 0x0205;
    private const int NIM_ADD               = 0x00;
    private const int NIM_DELETE            = 0x02;
    private const int NIM_SETVERSION        = 0x04;
    private const int NIF_MESSAGE           = 0x01;
    private const int NIF_ICON              = 0x02;
    private const int NIF_TIP              = 0x04;
    private const int NIIF_INFO             = 0x01;
    private const int NOTIFYICON_VERSION_4  = 4;

    // ── Win32 context menu ───────────────────────────────────────────────────
    private const int MF_STRING    = 0x00000000;
    private const int MF_SEPARATOR = 0x00000800;
    private const int TPM_RETURNCMD = 0x0100;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint  cbSize;
        public IntPtr hWnd;
        public uint  uID;
        public uint  uFlags;
        public uint  uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint  dwState;
        public uint  dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint  uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint  dwInfoFlags;
        public Guid  guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIcon(IntPtr hInst, string pszExeFileName, int nIconIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y,
        int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    // ── Menu item IDs ────────────────────────────────────────────────────────
    private const uint CMD_TOGGLE   = 1001;
    private const uint CMD_SETTINGS = 1002;
    private const uint CMD_QUIT     = 1003;

    // ── State ────────────────────────────────────────────────────────────────
    private readonly Window    _window;
    private readonly IntPtr    _hwnd;
    private readonly IntPtr    _hIcon;
    private readonly bool      _ownsIcon;   // true only when icon came from ExtractIcon (not a shared system icon)
    private readonly Func<bool> _isVisible;   // (#5) sync flag supplier — avoids WPF async staleness
    private NOTIFYICONDATA     _nid;
    private bool               _disposed;
    private HwndSource?        _hwndSource;

    public event Action? ToggleRequested;
    public event Action? OpenSettingsRequested;
    public event Action? QuitRequested;

    public TrayIcon(Window window, Func<bool> isVisible)
    {
        _window    = window;
        _isVisible = isVisible;
        _hwnd      = new WindowInteropHelper(window).Handle;

        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);

        (_hIcon, _ownsIcon) = LoadAppIcon();

        _nid = new NOTIFYICONDATA
        {
            cbSize           = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd             = _hwnd,
            uID              = 1,
            uFlags           = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon            = _hIcon,
            szTip            = "Quire — daily tech concepts",
        };

        Shell_NotifyIcon(NIM_ADD, ref _nid);

        // Set NOTIFYICON_VERSION_4 for proper taskbar positioning
        _nid.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref _nid);
    }

    // ── Icon loading ──────────────────────────────────────────────────────────

    /// <summary>
    /// Loads the first icon from the running executable so the tray shows the
    /// Quire icon instead of the generic Windows application icon.
    /// Falls back to IDI_APPLICATION if the exe has no embedded icon.
    /// Returns the icon handle and a flag indicating whether the caller owns it
    /// (only ExtractIcon returns an owned handle that must be freed with DestroyIcon;
    /// LoadIcon with a null hInstance returns a shared system handle that must NOT be freed).
    /// </summary>
    private static (IntPtr hIcon, bool ownsIcon) LoadAppIcon()
    {
        try
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exePath))
            {
                var icon = ExtractIcon(IntPtr.Zero, exePath, 0);
                if (icon != IntPtr.Zero) return (icon, true);
            }
        }
        catch { /* fall through to generic icon */ }

        // Fallback: shared system icon — must NOT be passed to DestroyIcon.
        return (LoadIcon(IntPtr.Zero, (IntPtr)32512), false);
    }

    // ── Win32 message pump hook ───────────────────────────────────────────────

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_TRAYICON) return IntPtr.Zero;

        var mouseMsg = (int)(lParam.ToInt64() & 0xFFFF);

        if (mouseMsg == WM_LBUTTONUP)
        {
            handled = true;
            ToggleRequested?.Invoke();
        }
        else if (mouseMsg == WM_RBUTTONUP)
        {
            handled = true;
            ShowContextMenu();
        }

        return IntPtr.Zero;
    }

    // ── Tray context menu ─────────────────────────────────────────────────────

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        try
        {
            // (#5) Use the synchronous flag — _window.IsVisible can lag after Hide()
            var label = _isVisible() ? "Hide widget" : "Show widget";
            AppendMenu(menu, MF_STRING,    CMD_TOGGLE,   label);
            AppendMenu(menu, MF_STRING,    CMD_SETTINGS, "AI Settings…");
            AppendMenu(menu, MF_SEPARATOR, 0,            string.Empty);
            AppendMenu(menu, MF_STRING,    CMD_QUIT,     "Quit Quire");

            // Must set foreground window so the menu dismisses on click-outside
            SetForegroundWindow(_hwnd);
            GetCursorPos(out var pt);

            var cmd = TrackPopupMenu(menu, TPM_RETURNCMD, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);

            switch (cmd)
            {
                case CMD_TOGGLE:   ToggleRequested?.Invoke();       break;
                case CMD_SETTINGS: OpenSettingsRequested?.Invoke(); break;
                case CMD_QUIT:     QuitRequested?.Invoke();         break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Shell_NotifyIcon(NIM_DELETE, ref _nid);
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;

        // Free the GDI icon handle to prevent a handle leak on every app exit.
        // Only icons loaded via ExtractIcon are owned — shared system icons (IDI_APPLICATION
        // loaded via LoadIcon with hInstance=0) must NOT be passed to DestroyIcon.
        if (_ownsIcon && _hIcon != IntPtr.Zero)
        {
            try { DestroyIcon(_hIcon); } catch { /* best-effort */ }
        }
    }
}
