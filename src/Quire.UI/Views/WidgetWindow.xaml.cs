using Quire.Application;
using Quire.Application.Schedulers;
using Quire.Domain;
using Quire.Infrastructure.AI;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SysPath = System.IO.Path;
using WpfApp = System.Windows.Application;

namespace Quire.UI.Views;

/// <summary>
/// The single widget window. All views (Compact / Expanded / Pinned / FirstRun / Error)
/// are visibility-toggled panels — no navigation, no secondary windows.
///
/// Window is always-on-top, hidden from Alt-Tab and taskbar (WS_EX_TOOLWINDOW).
/// Deactivated event drives Compact←Expanded so no global mouse hook is needed.
///
/// Audit fixes applied:
///   #1  — Right-click no longer collapses Expanded before menu fires (_contextMenuOpen guard)
///   #2  — Next button cross-fades content instead of snapping
///   #3  — Drag threshold raised to 6px for high-DPI reliability
///   #4  — Auto-collapse timer paused while mouse is over the expanded view
///   #5  — Tray menu label uses a bool flag, not Window.IsVisible (async-safe)
///   #7  — ReadMore disables button briefly; errors are swallowed gracefully
///   #15 — Keyboard navigation: Space/Enter expand, Escape collapses, Left/Right cycle
///   #19 — _currentIndex initialized to 2 so first %3 yields 0
///   #20 — QuotaDismiss only shows CompactView when a concept exists
///   #22 — WorkerW watchdog stopped immediately in ReapplySettingsAsync
/// </summary>
public partial class WidgetWindow : Window
{
    // ── Win32 ────────────────────────────────────────────────────────────────
    private const int GWL_EXSTYLE      = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW  = 0x00040000;
    private const int WM_USER          = 0x0400;
    private const int SPAWN_WORKER     = 0x052C;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, SendMessageTimeoutFlags fuFlags, uint uTimeout, out IntPtr lpdwResult);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetParent")]
    private static extern IntPtr GetParentWin32(IntPtr hWnd);

    [Flags]
    private enum SendMessageTimeoutFlags : uint
    {
        SMTO_NORMAL              = 0x0,
        SMTO_BLOCK               = 0x1,
        SMTO_ABORTIFHUNG         = 0x2,
        SMTO_NOTIMEOUTIFNOTHUNG  = 0x8
    }

    // ── Tunable constants ────────────────────────────────────────────────────
    private static readonly TimeSpan ExpandedTimeout   = TimeSpan.FromSeconds(30);
    private const long LowRamThresholdBytes            = 4L * 1024 * 1024 * 1024; // 4 GB
    /// <summary>
    /// Drag threshold in logical pixels. 6px is reliable across 100–200% DPI scaling
    /// without registering normal clicks as drags. (#3)
    /// </summary>
    private const double DragThreshold = 6.0;

    // ── Dependencies ─────────────────────────────────────────────────────────
    private readonly WidgetStateManager                    _stateManager;
    private readonly DailyConceptScheduler                 _dailyScheduler;
    private readonly RotationScheduler                     _rotationScheduler;
    private readonly ModelDownloadService                  _downloadService;
    private readonly CloudPrefetchService                  _prefetchService;
    private readonly ConceptGenerationBackgroundService    _bgService;
    private readonly RefreshScheduler                      _refreshScheduler;
    private readonly ISettingsStore                        _settingsStore;
    private readonly Func<SettingsWindow>                  _settingsWindowFactory;
    private readonly ILogger<WidgetWindow>                 _logger;

    // ── Runtime state ────────────────────────────────────────────────────────
    private Concept?          _currentConcept;
    /// <summary>
    /// Initialized to 2 so the first (_currentIndex + 1) % 3 = 0. (#19)
    /// </summary>
    private int               _currentIndex = 2;
    private string?           _pendingUpdateUrl;
    private AppSettings?      _cachedSettings;
    private DispatcherTimer?  _expandedTimer;
    private CancellationTokenSource? _downloadCts;
    private TrayIcon?         _trayIcon;
    private Point             _dragStartPoint;
    private DispatcherTimer?  _positionSaveTimer;
    private DispatcherTimer?  _workerWWatchdog;
    private IntPtr?           _originalParent;

    /// <summary>
    /// True while the widget's own ContextMenu is open.
    /// Prevents OnDeactivated from collapsing the widget before the menu fires. (#1)
    /// </summary>
    private bool _contextMenuOpen;

    /// <summary>
    /// Synchronous flag tracking widget visibility — used by TrayIcon so the
    /// menu label is never stale after Hide() before IsVisible propagates. (#5)
    /// </summary>
    private bool _isWidgetVisible = true;

    /// <summary>
    /// True while the mouse is over the expanded view — auto-collapse timer is
    /// paused to avoid collapsing while the user is actively reading. (#4)
    /// </summary>
    private bool _mouseOverExpanded;

    public WidgetWindow(
        WidgetStateManager                 stateManager,
        DailyConceptScheduler              dailyScheduler,
        RotationScheduler                  rotationScheduler,
        ModelDownloadService               downloadService,
        CloudPrefetchService               prefetchService,
        ConceptGenerationBackgroundService bgService,
        RefreshScheduler                   refreshScheduler,
        ISettingsStore                     settingsStore,
        Func<SettingsWindow>               settingsWindowFactory,
        ILogger<WidgetWindow>              logger)
    {
        _stateManager          = stateManager;
        _dailyScheduler        = dailyScheduler;
        _rotationScheduler     = rotationScheduler;
        _downloadService       = downloadService;
        _prefetchService       = prefetchService;
        _bgService             = bgService;
        _refreshScheduler      = refreshScheduler;
        _settingsStore         = settingsStore;
        _settingsWindowFactory = settingsWindowFactory;
        _logger                = logger;

        InitializeComponent();

        _stateManager.StateChanged        += OnStateChanged;
        _rotationScheduler.ConceptRotated += OnConceptRotated;

        _downloadService.ProgressChanged   += OnDownloadProgress;
        _downloadService.DownloadCompleted += OnDownloadCompleted;
        _downloadService.DownloadFailed    += OnDownloadFailed;

        // Wire context menu open/close so OnDeactivated can guard against #1
        ContextMenu.Opened += (_, _) => _contextMenuOpen = true;
        ContextMenu.Closed += (_, _) =>
        {
            _contextMenuOpen = false;
            // Do NOT re-fire OutsideClick here — the user may have dismissed the
            // context menu by clicking ON the widget (e.g. to expand it). That
            // click fires WidgetTrigger.Click first; firing OutsideClick immediately
            // after would collapse it back. OnDeactivated handles collapse correctly
            // when the user clicks genuinely outside. (#7 audit fix)
        };

        _ = ApplyPositionAndOpacityAsync();
    }

    // ── Window lifetime ───────────────────────────────────────────────────────

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyToolWindowStyle();
        _rotationScheduler.Start();

        _trayIcon = new TrayIcon(this, () => _isWidgetVisible); // (#5) pass flag supplier
        _trayIcon.ToggleRequested       += TrayToggle;
        _trayIcon.OpenSettingsRequested += () => OpenSettings_Click(this, new RoutedEventArgs());
        _trayIcon.QuitRequested         += () => WpfApp.Current.Shutdown();

        LocationChanged += OnLocationChanged;
        _ = ApplyWorkerWModeAsync();

        var sw = Stopwatch.GetTimestamp();
        _ = RunStartupChecksAsync().ContinueWith(_ =>
            _logger.LogInformation("Startup checks completed in {Ms:F1} ms.",
                Stopwatch.GetElapsedTime(sw).TotalMilliseconds));
    }

    protected override void OnClosed(EventArgs e)
    {
        _trayIcon?.Dispose();
        _expandedTimer?.Stop();
        _rotationScheduler.Stop();
        _rotationScheduler.Dispose();
        _downloadCts?.Cancel();
        _downloadCts?.Dispose();
        _positionSaveTimer?.Stop();
        _workerWWatchdog?.Stop();
        base.OnClosed(e);
    }

    // ── Tray icon callbacks ───────────────────────────────────────────────────

    private void TrayToggle()
    {
        if (_isWidgetVisible)
        {
            Hide();
            _isWidgetVisible = false;
            _logger.LogDebug("Widget hidden via tray.");
        }
        else
        {
            // Clear any held animation clock before showing, so the window
            // is never stuck at Opacity=0 from a previous AnimScaleOut run.
            BeginAnimation(OpacityProperty, null);
            Opacity = 1.0;
            Show();
            Activate();
            _isWidgetVisible = true;
            _logger.LogDebug("Widget shown via tray.");
        }
    }

    // ── Startup checks ────────────────────────────────────────────────────────

    private async Task RunStartupChecksAsync()
    {
        var settings = await _settingsStore.LoadAsync(CancellationToken.None);

        if (settings.IsFirstRun)
        {
            _logger.LogInformation("First run detected — showing setup choice screen.");
            Dispatcher.Invoke(ShowSetupChoiceView);
            return;
        }

        await ContinueAfterSetupChoiceAsync(settings);
    }

    private async Task ContinueAfterSetupChoiceAsync(AppSettings settings)
    {
        if (settings.Mode == "local")
        {
            var modelPath = GetModelPath(settings);

            if (!IsRamSufficient())
            {
                _logger.LogWarning("Low RAM detected (<4 GB). Recommending cloud mode.");
                Dispatcher.Invoke(ShowLowRamNudge);
                return;
            }

            if (!ModelDownloadService.IsModelPresent(modelPath))
            {
                _logger.LogInformation(
                    "Model not found at {Path}. Showing first-run download view.", modelPath);
                Dispatcher.Invoke(ShowFirstRunView);
                await StartModelDownloadAsync(settings.Provider.BaseUrl, modelPath);
            }
        }
    }

    private static string GetModelPath(AppSettings settings) =>
        SysPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Quire", "Models", $"{settings.Provider.Model}.gguf");

    private static bool IsRamSufficient()
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref mem) && mem.ullTotalPhys >= LowRamThresholdBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint  dwLength;
        public uint  dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private void ShowLowRamNudge()
    {
        CompactView.Visibility      = Visibility.Collapsed;
        FirstRunView.Visibility     = Visibility.Visible;
        DownloadStatusText.Text     =
            "Your system has less than 4 GB RAM. Local AI inference may be slow or unstable. " +
            "Consider switching to Cloud mode via AI Settings.";
        DownloadProgress.Visibility = Visibility.Collapsed;
    }

    // ── Model download wiring ─────────────────────────────────────────────────

    private async Task StartModelDownloadAsync(string baseUrl, string modelPath)
    {
        if (baseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
            baseUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Local endpoint detected — skipping model download.");
            Dispatcher.Invoke(() =>
            {
                FirstRunView.Visibility = Visibility.Collapsed;
                CompactView.Visibility  = Visibility.Visible;
            });
            return;
        }

        _downloadCts = new CancellationTokenSource();
        var modelUrl = $"{baseUrl.TrimEnd('/')}/models/download";
        await _downloadService.DownloadAsync(modelUrl, modelPath, _downloadCts.Token);
    }

    private void OnDownloadProgress(double percent) =>
        Dispatcher.Invoke(() =>
        {
            DownloadProgress.Value  = percent;
            DownloadStatusText.Text = $"Downloading… {percent:F0}%";
        });

    private void OnDownloadCompleted()
    {
        _logger.LogInformation("Model download completed.");
        Dispatcher.Invoke(() =>
        {
            FirstRunView.Visibility = Visibility.Collapsed;
            CompactView.Visibility  = Visibility.Visible;
        });
    }

    private void OnDownloadFailed(string message)
    {
        _logger.LogError("Download failed: {Message}", message);
        Dispatcher.Invoke(() =>
        {
            DownloadErrorText.Text        = message;
            DownloadErrorPanel.Visibility = Visibility.Visible;
            DownloadStatusText.Text       = "Download failed.";
        });
    }

    // ── Win32: hide from Alt-Tab / taskbar ────────────────────────────────────

    private void ApplyToolWindowStyle()
    {
        var hwnd    = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        exStyle = (exStyle | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
    }

    // ── Positioning ───────────────────────────────────────────────────────────

    private async Task ApplyPositionAndOpacityAsync()
    {
        var settings    = await _settingsStore.LoadAsync(CancellationToken.None);
        _cachedSettings = settings;

        if (settings.WidgetPosition is not null)
        {
            Left = settings.WidgetPosition.Left;
            Top  = settings.WidgetPosition.Top;
        }
        else
        {
            PositionTopRightDefault();
        }

        UpdateBackgroundOpacity(settings.WidgetOpacity);
    }

    private void PositionTopRightDefault()
    {
        var screen = SystemParameters.WorkArea;
        Left = screen.Right - Width - 20;
        Top  = 20;
    }

    private void UpdateBackgroundOpacity(double opacity)
    {
        // Clamp to 0.4–1.0 then convert to a 0–255 byte alpha value.
        // Only the background Rectangle's brush alpha changes — text, buttons,
        // and icons are siblings of the Rectangle, so they stay at full opacity.
        var clamped = Math.Clamp(opacity, 0.4, 1.0);
        var alpha   = (byte)Math.Round(clamped * 255);

        // Sync the brush color to the current theme background, then apply alpha.
        // This handles theme changes gracefully without a restart.
        if (TryGetThemeBackgroundColor(out var baseColor))
        {
            BackgroundBrush.Color = Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B);
        }
        else
        {
            // Fallback: just change the alpha on whatever color is currently set
            var c = BackgroundBrush.Color;
            BackgroundBrush.Color = Color.FromArgb(alpha, c.R, c.G, c.B);
        }
    }

    private static bool TryGetThemeBackgroundColor(out Color color)
    {
        if (System.Windows.Application.Current.Resources["BrushBackground"]
                is SolidColorBrush brush)
        {
            color = brush.Color;
            return true;
        }
        color = default;
        return false;
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        // Reuse the single timer — stop and restart the debounce window.
        // Creating a new DispatcherTimer on every LocationChanged event (which fires
        // on every drag pixel) allocates dozens of timers per drag operation.
        if (_positionSaveTimer is null)
        {
            _positionSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _positionSaveTimer.Tick += async (_, _) =>
            {
                _positionSaveTimer.Stop();
                await SavePositionAsync();
            };
        }
        _positionSaveTimer.Stop();
        _positionSaveTimer.Start();
    }

    private async Task SavePositionAsync()
    {
        var current = _cachedSettings ?? await _settingsStore.LoadAsync(CancellationToken.None);
        var updated = current with { WidgetPosition = new WindowPosition(Left, Top) };
        _cachedSettings = updated;
        await _settingsStore.SaveAsync(updated, CancellationToken.None);
    }

    // ── Drag-to-move (#3 threshold raised to 6px) ────────────────────────────

    private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
        {
            _dragStartPoint = e.GetPosition(this);
            MouseMove += OnWindowMouseMove;
            MouseUp   += OnWindowMouseUp;
        }
    }

    private void OnWindowMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var diff = e.GetPosition(this) - _dragStartPoint;
        if (Math.Abs(diff.X) > DragThreshold || Math.Abs(diff.Y) > DragThreshold)
        {
            MouseMove -= OnWindowMouseMove;
            MouseUp   -= OnWindowMouseUp;
            DragMove();
        }
    }

    private void OnWindowMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        MouseMove -= OnWindowMouseMove;
        MouseUp   -= OnWindowMouseUp;
    }

    // ── Keyboard navigation (#15) ─────────────────────────────────────────────

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case System.Windows.Input.Key.Space:
            case System.Windows.Input.Key.Enter:
                // Expand from Compact; ignored when already Expanded/Pinned
                if (_stateManager.Current == WidgetState.Compact)
                {
                    _stateManager.Fire(WidgetTrigger.Click);
                    e.Handled = true;
                }
                break;

            case System.Windows.Input.Key.Escape:
                // Collapse to Compact from any expanded state
                if (_stateManager.Current == WidgetState.Expanded)
                    _stateManager.Fire(WidgetTrigger.OutsideClick);
                else if (_stateManager.Current == WidgetState.Pinned)
                    _stateManager.Fire(WidgetTrigger.Unpin);
                e.Handled = true;
                break;

            case System.Windows.Input.Key.Right:
            case System.Windows.Input.Key.Down:
                // Advance to next concept while expanded or pinned
                if (_stateManager.Current == WidgetState.Expanded ||
                    _stateManager.Current == WidgetState.Pinned)
                {
                    StartExpandedTimer(); // reset timeout on keyboard nav too
                    _rotationScheduler.AdvanceNow();
                    e.Handled = true;
                }
                break;

            case System.Windows.Input.Key.P:
                // Toggle pin
                if (_stateManager.Current == WidgetState.Expanded)
                    _stateManager.Fire(WidgetTrigger.Pin);
                else if (_stateManager.Current == WidgetState.Pinned)
                    _stateManager.Fire(WidgetTrigger.Unpin);
                e.Handled = true;
                break;
        }
    }

    // ── Expanded view hover — pause auto-collapse (#4) ────────────────────────

    private void ExpandedView_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _mouseOverExpanded = true;
        _expandedTimer?.Stop();
        _logger.LogDebug("Mouse entered expanded view — auto-collapse paused.");
    }

    private void ExpandedView_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _mouseOverExpanded = false;
        // Restart the timer only if still in Expanded state (not Pinned)
        if (_stateManager.Current == WidgetState.Expanded)
        {
            StartExpandedTimer();
            _logger.LogDebug("Mouse left expanded view — auto-collapse resumed.");
        }
    }

    // ── WorkerW (pin behind desktop icons) ───────────────────────────────────

    private async Task ApplyWorkerWModeAsync()
    {
        var settings = await _settingsStore.LoadAsync(CancellationToken.None);
        if (!settings.PinBehindDesktopIcons) return;

        var hwnd = new WindowInteropHelper(this).Handle;
        _originalParent = GetParentWin32(hwnd);

        if (await TrySetWorkerWParentAsync(hwnd))
        {
            StartWorkerWWatchdog();
            _logger.LogInformation("Successfully reparented to WorkerW.");
        }
        else
        {
            _logger.LogWarning("Failed to find WorkerW — falling back to always-on-top.");
        }
    }

    private async Task<bool> TrySetWorkerWParentAsync(IntPtr hwnd)
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) { _logger.LogWarning("Progman not found."); return false; }

        SendMessageTimeout(progman, WM_USER + SPAWN_WORKER, IntPtr.Zero, IntPtr.Zero,
            SendMessageTimeoutFlags.SMTO_ABORTIFHUNG, 1000, out _);

        var workerW = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
        if (workerW == IntPtr.Zero) { _logger.LogWarning("WorkerW not found."); return false; }

        var shell = FindWindowEx(workerW, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (shell != IntPtr.Zero)
        {
            // The first WorkerW holds the desktop icons shell — the *second* one is
            // the actual behind-desktop layer we want. If there is no second WorkerW
            // (e.g. on systems without desktop icons visible), bail out rather than
            // passing IntPtr.Zero to SetParent, which would reparent to the desktop root.
            var nextWorkerW = FindWindowEx(progman, workerW, "WorkerW", null);
            if (nextWorkerW == IntPtr.Zero)
            {
                _logger.LogWarning("Second WorkerW not found after SHELLDLL_DefView detected — aborting reparent.");
                return false;
            }
            workerW = nextWorkerW;
        }

        var result = SetParent(hwnd, workerW);
        if (result == IntPtr.Zero)
        {
            _logger.LogWarning("SetParent failed: {Error}", Marshal.GetLastWin32Error());
            return false;
        }
        return true;
    }

    private void StartWorkerWWatchdog()
    {
        _workerWWatchdog?.Stop();
        _workerWWatchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _workerWWatchdog.Tick += async (_, _) =>
        {
            var settings = await _settingsStore.LoadAsync(CancellationToken.None);
            if (!settings.PinBehindDesktopIcons)
            {
                _workerWWatchdog?.Stop();
                return;
            }

            var hwnd          = new WindowInteropHelper(this).Handle;
            var currentParent = GetParentWin32(hwnd);
            var workerW       = FindWorkerW();

            if (workerW == IntPtr.Zero || currentParent != workerW)
            {
                _logger.LogInformation("WorkerW parent lost — reattaching.");
                if (!await TrySetWorkerWParentAsync(hwnd))
                {
                    _logger.LogWarning("Reattach failed — stopping watchdog.");
                    _workerWWatchdog?.Stop();
                }
            }
        };
        _workerWWatchdog.Start();
    }

    private IntPtr FindWorkerW()
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return IntPtr.Zero;

        SendMessageTimeout(progman, WM_USER + SPAWN_WORKER, IntPtr.Zero, IntPtr.Zero,
            SendMessageTimeoutFlags.SMTO_ABORTIFHUNG, 1000, out _);

        var workerW = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
        if (workerW == IntPtr.Zero) return IntPtr.Zero;

        var shell = FindWindowEx(workerW, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (shell != IntPtr.Zero)
            workerW = FindWindowEx(progman, workerW, "WorkerW", null);

        return workerW;
    }

    private async Task RestoreNormalParentAsync()
    {
        if (_originalParent.HasValue)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetParent(hwnd, _originalParent.Value);
            _workerWWatchdog?.Stop();
            _workerWWatchdog = null; // (#22) null out so ReapplySettingsAsync can re-check cleanly
            _logger.LogInformation("Restored normal parent window.");
        }
    }

    // ── Auto-collapse timer ───────────────────────────────────────────────────

    private void StartExpandedTimer()
    {
        _expandedTimer?.Stop();
        // Don't start if mouse is over the expanded view (#4)
        if (_mouseOverExpanded) return;

        _expandedTimer = new DispatcherTimer { Interval = ExpandedTimeout };
        _expandedTimer.Tick += (_, _) =>
        {
            _expandedTimer.Stop();
            // Guard again — user might have moved mouse in during the 30s (#4)
            if (!_mouseOverExpanded)
                _stateManager.Fire(WidgetTrigger.Timeout);
        };
        _expandedTimer.Start();
    }

    private void StopExpandedTimer() => _expandedTimer?.Stop();

    // ── Deactivated → Compact (#1 right-click guard) ─────────────────────────

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);

        // Guard 1: don't collapse when a settings/owned window takes focus
        foreach (Window owned in OwnedWindows)
            if (owned.IsVisible) return;

        // Guard 2: don't collapse when our own context menu is open (#1)
        if (_contextMenuOpen) return;

        _stateManager.Fire(WidgetTrigger.OutsideClick);
    }

    // ── State machine → view transitions ─────────────────────────────────────

    private void OnStateChanged(WidgetState state)
    {
        Dispatcher.Invoke(() =>
        {
            switch (state)
            {
                case WidgetState.Compact:
                    StopExpandedTimer();
                    _mouseOverExpanded = false;
                    ShowCompact();
                    break;
                case WidgetState.Expanded:
                    StartExpandedTimer();
                    ShowExpanded();
                    break;
                case WidgetState.Pinned:
                    StopExpandedTimer();
                    break;
            }
        });
    }

    // ── View helpers ──────────────────────────────────────────────────────────

    private void ShowSetupChoiceView()
    {
        CompactView.Visibility     = Visibility.Collapsed;
        ExpandedView.Visibility    = Visibility.Collapsed;
        ErrorView.Visibility       = Visibility.Collapsed;
        FirstRunView.Visibility    = Visibility.Collapsed;
        SetupChoiceView.Visibility = Visibility.Visible;
    }

    private void ShowFirstRunView()
    {
        CompactView.Visibility  = Visibility.Collapsed;
        ExpandedView.Visibility = Visibility.Collapsed;
        ErrorView.Visibility    = Visibility.Collapsed;
        FirstRunView.Visibility = Visibility.Visible;
    }

    private void ShowCompact()
    {
        if (ExpandedView.Visibility == Visibility.Visible)
        {
            // Scale the whole window out, then swap visibility and fade the compact badge in.
            // AnimScaleOut targets Window.RenderTransform (ScaleTransform, CenterX=160).
            var scaleOut = ((Storyboard)FindResource("AnimScaleOut")).Clone();
            scaleOut.Completed += (_, _) =>
            {
                ExpandedView.Visibility = Visibility.Collapsed;
                CompactView.Visibility  = Visibility.Visible;
                PinButton.IsChecked     = false;

                // Restore scale to 1 so the next expand starts from full size.
                // Also release the ScaleX/Y animation clocks — HoldEnd would otherwise
                // hold 0.85 as the animated base value and fight the direct property write.
                if (RenderTransform is ScaleTransform st)
                {
                    st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    st.ScaleX = 1.0;
                    st.ScaleY = 1.0;
                }

                // Clear the animation clock that AnimScaleOut left holding Opacity at 0.
                // Without this, WPF's FillBehavior.HoldEnd keeps the window invisible.
                BeginAnimation(OpacityProperty, null);
                Opacity = 1.0;

                ((Storyboard)FindResource("AnimFadeIn")).Begin(CompactView);
            };
            scaleOut.Begin(this);
        }
        else
        {
            CompactView.Visibility = Visibility.Visible;
        }
        QuotaView.Visibility = Visibility.Collapsed;
        UpdateBadgeLabel();
    }

    private void ShowExpanded()
    {
        CompactView.Visibility  = Visibility.Collapsed;
        ExpandedView.Visibility = Visibility.Visible;
        ErrorView.Visibility    = Visibility.Collapsed;
        FirstRunView.Visibility = Visibility.Collapsed;
        QuotaView.Visibility    = Visibility.Collapsed;
        UpdateExpandedContent();
        // Slide the expanded panel up (TranslateTransform on ExpandedView)
        // and simultaneously scale the whole window in (ScaleTransform on Window).
        // Clone both storyboards — calling Begin() on the shared resource instance
        // mutates it in place and can cause the From value to not reset on the second call.
        ((Storyboard)FindResource("AnimSlideInUp")).Clone().Begin(ExpandedView);
        ((Storyboard)FindResource("AnimScaleIn")).Clone().Begin(this);
    }

    private void ShowError(string message)
    {
        Dispatcher.Invoke(() =>
        {
            ErrorMessageText.Text   = message;
            CompactView.Visibility  = Visibility.Collapsed;
            ExpandedView.Visibility = Visibility.Collapsed;
            FirstRunView.Visibility = Visibility.Collapsed;
            QuotaView.Visibility    = Visibility.Collapsed;
            ErrorView.Visibility    = Visibility.Visible;
        });
    }

    // ── Concept display ───────────────────────────────────────────────────────

    private void UpdateBadgeLabel()
    {
        if (_currentConcept is not null)
        {
            CompactCategory.Text      = _currentConcept.Category;
            CompactSlotIndicator.Text = $"· {_currentIndex + 1}/3";
            CompactTitle.Text         = _currentConcept.Title;
            // Truncate at a word boundary near 70 chars — avoids mid-word ellipsis (#5)
            var teaser = TruncateAtWord(_currentConcept.Explanation, 70);
            CompactTeaser.Text = teaser;
        }
        else
        {
            CompactCategory.Text      = string.Empty;
            CompactSlotIndicator.Text = string.Empty;
            CompactTitle.Text         = "Loading…";
            CompactTeaser.Text        = "Today's concept";
        }
    }

    private void UpdateExpandedContent()
    {
        if (_currentConcept is null) return;
        TitleText.Text       = _currentConcept.Title;
        ExplanationText.Text = _currentConcept.Explanation;
        CategoryTag.Text     = _currentConcept.Category;
        UpdateDots();
    }

    /// <summary>
    /// Cross-fades the title and explanation when content changes while expanded.
    /// Fades a wrapper panel as a single unit to prevent the race condition where
    /// two independent fade-outs complete at different times and swap content
    /// at different moments. (#3 audit fix)
    /// Falls back to instant update if the expanded view isn't visible.
    /// </summary>
    private void UpdateExpandedContentAnimated()
    {
        if (_currentConcept is null) return;
        if (ExpandedView.Visibility != Visibility.Visible)
        {
            UpdateExpandedContent();
            return;
        }

        // Snapshot the new content before any async gap
        var newTitle       = _currentConcept.Title;
        var newExplanation = _currentConcept.Explanation;
        var newCategory    = _currentConcept.Category;
        var newIndex       = _currentIndex;

        // Cancel any in-progress fade animation clock before starting a new one.
        // Without this, rapid AdvanceNow() calls stack two simultaneous Opacity clocks
        // on ExpandedView and the HoldEnd from the previous AnimFadeOut fights the new one.
        ExpandedView.BeginAnimation(UIElement.OpacityProperty, null);
        ExpandedView.Opacity = 1.0;

        // Fade out the whole ExpandedView content panel as one unit (#3)
        var fadeOut = ((Storyboard)FindResource("AnimFadeOut")).Clone();
        Storyboard.SetTarget(fadeOut, ExpandedView);
        fadeOut.Completed += (_, _) =>
        {
            // Swap all content while fully invisible — no partial-swap flash
            TitleText.Text       = newTitle;
            ExplanationText.Text = newExplanation;
            CategoryTag.Text     = newCategory;

            var active   = (SolidColorBrush)FindResource("BrushPrimary");
            var inactive = (SolidColorBrush)FindResource("BrushBorderStrong");
            Dot1.Fill = newIndex == 0 ? active : inactive;
            Dot2.Fill = newIndex == 1 ? active : inactive;
            Dot3.Fill = newIndex == 2 ? active : inactive;

            // Fade the whole panel back in
            var fadeIn = ((Storyboard)FindResource("AnimFadeIn")).Clone();
            Storyboard.SetTarget(fadeIn, ExpandedView);
            fadeIn.Begin();
        };
        fadeOut.Begin();
    }

    private void UpdateDots()
    {
        var active   = (SolidColorBrush)FindResource("BrushPrimary");
        var inactive = (SolidColorBrush)FindResource("BrushBorderStrong");
        Dot1.Fill = _currentIndex == 0 ? active : inactive;
        Dot2.Fill = _currentIndex == 1 ? active : inactive;
        Dot3.Fill = _currentIndex == 2 ? active : inactive;
    }

    /// <summary>
    /// Truncates text at the last word boundary at or before <paramref name="maxChars"/>.
    /// Prevents mid-word ellipsis like "…connectio…" (#5 audit fix).
    /// </summary>
    private static string TruncateAtWord(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        // Find the last space at or before maxChars
        var lastSpace = text.LastIndexOf(' ', maxChars);
        var cutAt = lastSpace > 0 ? lastSpace : maxChars;
        return text[..cutAt].TrimEnd() + "…";
    }

    // ── Scheduler callbacks ───────────────────────────────────────────────────

    public void OnConceptSetReady(DailyConceptSet set)
    {
        _logger.LogInformation("New DailyConceptSet received for {Date}.", set.Date);
        // _currentIndex is reset to 2 at field init; RotationScheduler.LoadSet fires
        // ConceptRotated immediately which increments to 0. (#19)
        _currentIndex = 2;
        _rotationScheduler.LoadSet(set);
        _ = ShowTrayHintIfNeededAsync();
    }

    public void OnGenerationFailed(Exception ex)
    {
        _logger.LogError(ex, "Concept generation failed.");
        ShowError(
            "The AI model couldn't generate a concept right now. " +
            "Check that your AI endpoint is running (or internet is available), then retry.");
    }

    public void OnQuotaExceeded()
    {
        _logger.LogWarning("Quota exceeded — showing QuotaView.");
        Dispatcher.Invoke(() =>
        {
            CompactView.Visibility  = Visibility.Collapsed;
            ExpandedView.Visibility = Visibility.Collapsed;
            ErrorView.Visibility    = Visibility.Collapsed;
            FirstRunView.Visibility = Visibility.Collapsed;
            QuotaView.Visibility    = Visibility.Visible;
        });
    }

    public void OnUpdateAvailable(string version, string downloadUrl)
    {
        _logger.LogInformation("Update available: v{Version}.", version);
        _pendingUpdateUrl = downloadUrl;
        Dispatcher.Invoke(() =>
        {
            UpdateBannerText.Text   = $"Version {version} is available.";
            UpdateBanner.Visibility = Visibility.Visible;
        });
    }

    private void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        UpdateBanner.Visibility = Visibility.Collapsed;
        var url = _pendingUpdateUrl;
        _pendingUpdateUrl = null;
        if (url is null) return;
        _ = Task.Run(() => _refreshScheduler.PerformUpdateAsync(url, CancellationToken.None));
    }

    private void UpdateLater_Click(object sender, RoutedEventArgs e) =>
        UpdateBanner.Visibility = Visibility.Collapsed;

    private void OnConceptRotated(Concept concept)
    {
        Dispatcher.Invoke(() =>
        {
            _currentConcept = concept;
            _currentIndex   = (_currentIndex + 1) % 3;
            UpdateBadgeLabel();
            // Use animated swap when expanded (#2), instant when compact
            if (ExpandedView.Visibility == Visibility.Visible)
                UpdateExpandedContentAnimated();
        });
    }

    // ── UI event handlers ─────────────────────────────────────────────────────

    private void CompactView_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => _stateManager.Fire(WidgetTrigger.Click);

    private void PinButton_Checked(object sender, RoutedEventArgs e)
    {
        _stateManager.Fire(WidgetTrigger.Pin);
        // (#8) Update automation name to reflect pinned state
        System.Windows.Automation.AutomationProperties.SetName(PinButton, "Unpin concept");
    }

    private void PinButton_Unchecked(object sender, RoutedEventArgs e)
    {
        _stateManager.Fire(WidgetTrigger.Unpin);
        // (#8) Update automation name to reflect unpinned state
        System.Windows.Automation.AutomationProperties.SetName(PinButton, "Pin concept");
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        StartExpandedTimer();
        _rotationScheduler.AdvanceNow();
        _logger.LogDebug("User pressed Next.");
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_currentConcept is null) return;
        Clipboard.SetText($"{_currentConcept.Title}\n\n{_currentConcept.Explanation}");
        _logger.LogInformation("Copied to clipboard: {Title}", _currentConcept.Title);
    }

    /// <summary>
    /// Opens a web search for the concept. Briefly disables the button as feedback,
    /// then silently swallows any launch error. (#7)
    /// </summary>
    private void ReadMore_Click(object sender, RoutedEventArgs e)
    {
        if (_currentConcept is null) return;

        // Brief visual feedback — disable for 1.2s (#7)
        ReadMoreButton.IsEnabled = false;
        var restoreTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        restoreTimer.Tick += (_, _) => { restoreTimer.Stop(); ReadMoreButton.IsEnabled = true; };
        restoreTimer.Start();

        var query = Uri.EscapeDataString(_currentConcept.Title);
        OpenUrl($"https://www.google.com/search?q={query}");
    }

    /// <summary>Opens a URL via ShellExecuteEx — the same call Windows uses for hyperlinks. Never throws. (#7)</summary>
    private void OpenUrl(string url)
    {
        try
        {
            var info = new SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                fMask  = 0x00000040, // SEE_MASK_NOCLOSEPROCESS
                hwnd   = IntPtr.Zero,
                lpVerb = "open",
                lpFile = url,
                nShow  = 1 // SW_SHOWNORMAL
            };
            if (!ShellExecuteEx(ref info))
            {
                var err = Marshal.GetLastWin32Error();
                _logger.LogWarning("ShellExecuteEx failed (error {Error}) for URL: {Url}", err, url);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open browser for URL: {Url}", url);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int    cbSize;
        public uint   fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int    nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public IntPtr hkeyClass;
        public uint   dwHotKey;
        public IntPtr hIconOrMonitor;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

    private void ErrorRetry_Click(object sender, RoutedEventArgs e)
    {
        ErrorView.Visibility   = Visibility.Collapsed;
        CompactView.Visibility = Visibility.Visible;
        _ = Task.Run(async () =>
        {
            try
            {
                var settings = await _settingsStore.LoadAsync(CancellationToken.None);
                if (settings.Mode == "cloud")
                {
                    await _prefetchService.RefillIfConnectedAsync(CancellationToken.None);
                    var set = await _prefetchService.TryConsumeAsync(CancellationToken.None);
                    if (set is not null) OnConceptSetReady(set);
                    else OnGenerationFailed(new InvalidOperationException(
                        "Cloud buffer is still empty. Check your internet connection."));
                }
                else
                {
                    await _bgService.ForceRetryAsync(CancellationToken.None);
                }
            }
            catch (QuotaExceededException)
            {
                // ForceRetryAsync can propagate a 429 when the user retries on an exhausted quota.
                // Surface the quota view instead of the generic error view.
                OnQuotaExceeded();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ErrorRetry_Click background task failed.");
                OnGenerationFailed(ex);
            }
        });
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        var settingsWin = _settingsWindowFactory();
        settingsWin.Owner  = this;
        settingsWin.Closed += async (_, _) =>
        {
            try   { await ReapplySettingsAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to reapply settings after window closed."); }
        };
        settingsWin.ShowDialog();
    }

    private async Task ReapplySettingsAsync()
    {
        var settings    = await _settingsStore.LoadAsync(CancellationToken.None);
        _cachedSettings = settings;
        UpdateBackgroundOpacity(settings.WidgetOpacity);

        if (settings.PinBehindDesktopIcons)
        {
            // Only attach if watchdog isn't already running (#22)
            if (_workerWWatchdog == null)
                await ApplyWorkerWModeAsync();
        }
        else
        {
            // Immediately stop the watchdog and restore parent (#22)
            await RestoreNormalParentAsync();
        }
    }

    private void DownloadRetry_Click(object sender, RoutedEventArgs e)
    {
        DownloadErrorPanel.Visibility = Visibility.Collapsed;
        DownloadStatusText.Text       = "Retrying download…";
        DownloadProgress.Value        = 0;
        DownloadProgress.Visibility   = Visibility.Visible;

        // Cancel and replace the CTS on the UI thread before spawning the background task,
        // so there is no race between the task reading/writing _downloadCts and OnClosed.
        _downloadCts?.Cancel();
        _downloadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _downloadCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                var settings  = await _settingsStore.LoadAsync(cts.Token);
                var modelPath = GetModelPath(settings);
                await _downloadService.DownloadAsync(
                    settings.Provider.BaseUrl, modelPath, cts.Token);
            }
            catch (OperationCanceledException) { /* download was cancelled — no error needed */ }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DownloadRetry_Click background task failed.");
                OnDownloadFailed(ex.Message);
            }
        });
    }

    private void SkipToCloud_Click(object sender, RoutedEventArgs e)
    {
        _downloadCts?.Cancel();
        FirstRunView.Visibility = Visibility.Collapsed;
        var settingsWin = _settingsWindowFactory();
        settingsWin.PreSelectMode("cloud");
        settingsWin.Owner  = this;
        // Wire ReapplySettingsAsync the same way OpenSettings_Click does — this ensures
        // the opacity, WorkerW mode, and scheduler state are refreshed after the dialog.
        // Also avoids unconditionally showing CompactView before any concept exists.
        settingsWin.Closed += async (_, _) =>
        {
            try   { await ReapplySettingsAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to reapply settings after SkipToCloud dialog."); }
        };
        settingsWin.ShowDialog();
        // CompactView is shown by ReapplySettingsAsync / the concept delivery pipeline,
        // not forced here — avoids "Loading…" state with no concept loaded.
    }

    // ── Setup choice handlers ─────────────────────────────────────────────────

    private void SetupChooseLocal_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => _ = ApplySetupChoiceAsync("local");

    private void SetupChooseCloud_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => _ = ApplySetupChoiceAsync("cloud");

    private async Task ApplySetupChoiceAsync(string mode)
    {
        _logger.LogInformation("Setup choice: user selected '{Mode}'.", mode);
        var current = await _settingsStore.LoadAsync(CancellationToken.None);
        var updated = current with { Mode = mode, IsFirstRun = false };
        await _settingsStore.SaveAsync(updated, CancellationToken.None);
        Dispatcher.Invoke(() => SetupChoiceView.Visibility = Visibility.Collapsed);
        await ContinueAfterSetupChoiceAsync(updated);
        await ShowTrayHintIfNeededAsync();
        Dispatcher.Invoke(() =>
        {
            if (CompactView.Visibility  != Visibility.Visible
             && FirstRunView.Visibility != Visibility.Visible
             && ErrorView.Visibility    != Visibility.Visible)
                CompactView.Visibility = Visibility.Visible;
        });
    }

    /// <summary>
    /// Dismisses the quota view. Only shows CompactView when a concept exists,
    /// to avoid showing "Loading…" with no content. (#20)
    /// </summary>
    private void QuotaDismiss_Click(object sender, RoutedEventArgs e)
    {
        QuotaView.Visibility = Visibility.Collapsed;
        if (_currentConcept is not null)
        {
            CompactView.Visibility = Visibility.Visible;
        }
        else
        {
            // No concept available yet — show a minimal "nothing here" state
            CompactTitle.Text         = "No concept yet";
            CompactTeaser.Text        = "Concepts will resume tomorrow.";
            CompactCategory.Text      = string.Empty;
            CompactSlotIndicator.Text = string.Empty;
            CompactView.Visibility    = Visibility.Visible;
        }
        _logger.LogInformation("Quota view dismissed by user.");
    }

    private void CtxPin_Click(object sender, RoutedEventArgs e)
    {
        if (_stateManager.Current == WidgetState.Pinned)
            _stateManager.Fire(WidgetTrigger.Unpin);
        else if (_stateManager.Current == WidgetState.Expanded)
            _stateManager.Fire(WidgetTrigger.Pin);
        else
            _stateManager.Fire(WidgetTrigger.Click);
    }

    private void CtxCopy_Click(object sender, RoutedEventArgs e) => Copy_Click(sender, e);
    private void CtxQuit_Click(object sender, RoutedEventArgs e) => WpfApp.Current.Shutdown();

    // ── Public surface for SettingsWindow opacity preview ────────────────────

    public void SetBackgroundOpacity(double opacity) => UpdateBackgroundOpacity(opacity);

    // ── Close-to-tray + visibility flag (#5) ─────────────────────────────────

    private void CloseToTray_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _isWidgetVisible = false;
        _logger.LogInformation("Widget hidden to tray via × button.");
    }

    private void TrayHint_Dismiss(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        TrayHintBanner.Visibility = Visibility.Collapsed;
        _ = Task.Run(async () =>
        {
            var current = await _settingsStore.LoadAsync(CancellationToken.None);
            if (!current.HasSeenTrayHint)
                await _settingsStore.SaveAsync(
                    current with { HasSeenTrayHint = true }, CancellationToken.None);
        });
        _logger.LogInformation("Tray hint dismissed.");
    }

    private async Task ShowTrayHintIfNeededAsync()
    {
        var settings = await _settingsStore.LoadAsync(CancellationToken.None);
        if (settings.HasSeenTrayHint) return;
        Dispatcher.Invoke(() => TrayHintBanner.Visibility = Visibility.Visible);
    }
}
