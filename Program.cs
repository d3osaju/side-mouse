using Microsoft.Win32;
using static SideMouse.Native;

namespace SideMouse;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "SideMouse.SingleInstance", out bool firstInstance);
        if (!firstInstance) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new SideMouseApp());
    }
}

/// <summary>
/// Toggle with the hotkey. While active, the physical mouse is detached from the game:
/// movement drives a fake cursor on the side monitor, and wheel/clicks are posted
/// straight to the window under it, so nothing ever steals focus from the game.
/// </summary>
sealed class SideMouseApp : ApplicationContext
{
    readonly CursorOverlay _overlay = new();
    readonly MouseHook _hook;
    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _toggleItem;
    readonly ToolStripMenuItem _screenMenu;
    readonly ToolStripMenuItem _shortcutMenu;

    Shortcut _shortcut = Shortcut.Load();

    Screen _target;
    Point _pos;
    bool _active;

    public SideMouseApp()
    {
        _target = DefaultTarget();
        _pos = Center(_target.Bounds);

        _overlay.HotkeyPressed += Toggle;
        _overlay.EscapePressed += () => { if (_active) Toggle(); };
        _hook = new MouseHook { Handler = OnMouse };
        _restoreTimer.Tick += (_, _) => RestoreRealCursor();

        _toggleItem = new ToolStripMenuItem("Control side screen", null, (_, _) => Toggle());
        _screenMenu = new ToolStripMenuItem("Side screen");
        _shortcutMenu = new ToolStripMenuItem("Shortcut");
        foreach (var preset in Shortcut.Presets)
            _shortcutMenu.DropDownItems.Add(preset.Name, null, (_, _) => ApplyShortcut(preset, save: true));
        var menu = new ContextMenuStrip();
        menu.Items.Add(_toggleItem);
        menu.Items.Add(_screenMenu);
        menu.Items.Add(_shortcutMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        menu.Opening += (_, _) => RebuildScreenMenu();

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => Toggle();
        ApplyShortcut(_shortcut, save: false);

        SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
    }

    static Screen DefaultTarget() =>
        Screen.AllScreens.FirstOrDefault(s => !s.Primary) ?? Screen.PrimaryScreen!;

    static Point Center(Rectangle r) => new(r.Left + r.Width / 2, r.Top + r.Height / 2);

    void ApplyShortcut(Shortcut shortcut, bool save)
    {
        if (shortcut.IsMouse)
            _overlay.UnregisterHotkey();
        else if (!_overlay.RegisterHotkey(shortcut.Mods, shortcut.Key))
        {
            MessageBox.Show($"Couldn't use {shortcut.Name} - another app already has it.", "SideMouse");
            return;
        }

        _shortcut = shortcut;
        if (save) shortcut.Save();
        _tray.Text = $"SideMouse - {shortcut.Name} to toggle";
        _toggleItem.Text = $"Control side screen ({shortcut.Name})";
        foreach (ToolStripMenuItem item in _shortcutMenu.DropDownItems)
            item.Checked = item.Text == shortcut.Name;
    }

    void Toggle()
    {
        _active = !_active;
        _toggleItem.Checked = _active;

        if (_active)
        {
            // Remember exactly how the game left the cursor (where it is and the rectangle
            // the game confines it to) so we can hand it back untouched. Then release the
            // confinement so it can't skew the deltas we read from the hook.
            GetCursorPos(out _gameCursor);
            GetClipCursor(out _gameClip);
            ClipCursor(IntPtr.Zero);
            _overlay.MoveTipTo(_pos);
            _overlay.Show();
            _overlay.GrabEscape();
        }
        else
        {
            _overlay.ReleaseEscape();
            _overlay.Hide();
            _buttonsDown = 0;
            _pressHwnd = IntPtr.Zero;
            RestoreRealCursor();
            // Re-lock the cursor to the game; otherwise it stays free to wander onto the side
            // screen until the game re-confines it (e.g. after a trip through its menu).
            ClipCursor(ref _gameClip);
        }
    }

    bool OnMouse(int msg, MSLLHOOKSTRUCT data)
    {
        if ((data.flags & LLMHF_INJECTED) != 0) return false;

        // Mouse-button shortcut: works whether or not we're active, and the game never sees it.
        if (_shortcut.IsMouse && _shortcut.MatchesMouse(msg, data.mouseData, out bool down))
        {
            if (down) _overlay.BeginInvoke(Toggle); // keep the hook callback quick
            return true;
        }

        if (!_active) return false;

        switch (msg)
        {
            case WM_MOUSEMOVE:
                // The event is swallowed, so the real cursor stays put and pt - cursor is the
                // movement (already including the user's pointer speed/acceleration).
                GetCursorPos(out var real);
                int dx = data.pt.X - real.X, dy = data.pt.Y - real.Y;
                // A huge jump means the real cursor was repositioned (by us or the game)
                // between the event and now - not something a hand can do in one report.
                if (Math.Abs(dx) < 400 && Math.Abs(dy) < 400) MoveFake(dx, dy);
                return true;

            case WM_MOUSEWHEEL:
            case WM_MOUSEHWHEEL:
                PostWheel(msg, (short)(data.mouseData >> 16));
                return true;

            case WM_LBUTTONDOWN or WM_LBUTTONUP or WM_RBUTTONDOWN or WM_RBUTTONUP
                or WM_MBUTTONDOWN or WM_MBUTTONUP:
                PostButton(msg);
                return true;

            case WM_XBUTTONDOWN or WM_XBUTTONUP:
                return true; // keep side buttons away from the game too

            default:
                return false;
        }
    }

    void MoveFake(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        var b = _target.Bounds;
        _pos = new Point(Math.Clamp(_pos.X + dx, b.Left, b.Right - 1),
                         Math.Clamp(_pos.Y + dy, b.Top, b.Bottom - 1));
        _overlay.MoveTipTo(_pos);

        // Mid-press: keep the real cursor under the fake one and feed the captured window
        // button-held moves, so drag-select and drag-and-drop follow the fake cursor.
        if (_pressHwnd != IntPtr.Zero)
        {
            ParkRealCursor();
            PostMessage(_pressHwnd, WM_MOUSEMOVE, (IntPtr)_buttonsDown, ClientLParam(_pressHwnd));
        }
    }

    IntPtr TargetWindow() =>
        WindowFromPoint(new POINT { X = _pos.X, Y = _pos.Y });

    IntPtr ClientLParam(IntPtr hwnd)
    {
        var client = new POINT { X = _pos.X, Y = _pos.Y };
        ScreenToClient(hwnd, ref client);
        return MakeLParam(client.X, client.Y);
    }

    void PostWheel(int msg, short delta)
    {
        var hwnd = TargetWindow();
        if (hwnd == IntPtr.Zero) return;
        // Wheel messages carry screen coordinates; DefWindowProc bubbles them up to a
        // parent that scrolls if the child under the point doesn't.
        PostMessage(hwnd, msg, (IntPtr)(delta << 16), MakeLParam(_pos.X, _pos.Y));
    }

    int _buttonsDown;
    IntPtr _pressHwnd;        // window that got the first button-down, like mouse capture
    POINT _gameCursor;        // where the game had the real cursor when we took over
    RECT _gameClip;           // the game's cursor confinement at that moment
    readonly System.Windows.Forms.Timer _restoreTimer = new() { Interval = 80 };

    // Windows only synthesises double-clicks from real input, so detect them ourselves.
    int _lastDownMsg;
    long _lastDownTick;
    Point _lastDownPos;

    void ParkRealCursor()
    {
        // Apps cross-check GetCursorPos / DragDetect against the click; if the real cursor
        // is back on the game screen they think the mouse already moved and start a drag.
        // Moving the cursor never changes focus, so the game stays in front.
        ClipCursor(IntPtr.Zero);
        SetCursorPos(_pos.X, _pos.Y);
    }

    void PostButton(int msg)
    {
        bool isDown = msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN;

        if (isDown && _buttonsDown == 0)
        {
            _restoreTimer.Stop();
            _pressHwnd = TargetWindow();
        }
        var hwnd = _pressHwnd != IntPtr.Zero ? _pressHwnd : TargetWindow();
        if (hwnd == IntPtr.Zero) return;

        _buttonsDown = msg switch
        {
            WM_LBUTTONDOWN => _buttonsDown | MK_LBUTTON,
            WM_LBUTTONUP => _buttonsDown & ~MK_LBUTTON,
            WM_RBUTTONDOWN => _buttonsDown | MK_RBUTTON,
            WM_RBUTTONUP => _buttonsDown & ~MK_RBUTTON,
            WM_MBUTTONDOWN => _buttonsDown | MK_MBUTTON,
            WM_MBUTTONUP => _buttonsDown & ~MK_MBUTTON,
            _ => _buttonsDown,
        };

        ParkRealCursor();
        var lParam = ClientLParam(hwnd);
        int post = isDown ? DoubleClickOrDown(msg, hwnd) : msg;

        // A move first, so apps that hit-test on hover know where the click lands.
        PostMessage(hwnd, WM_MOUSEMOVE, (IntPtr)_buttonsDown, lParam);
        PostMessage(hwnd, post, (IntPtr)_buttonsDown, lParam);

        if (_buttonsDown == 0)
        {
            _pressHwnd = IntPtr.Zero;
            // Give the app a moment to process the button-up before the cursor jumps away.
            _restoreTimer.Start();
        }
    }

    void RestoreRealCursor()
    {
        _restoreTimer.Stop();
        if (_buttonsDown != 0) return;
        SetCursorPos(_gameCursor.X, _gameCursor.Y);
    }

    int DoubleClickOrDown(int msg, IntPtr hwnd)
    {
        long now = Environment.TickCount64;
        bool isDouble = msg == _lastDownMsg
            && now - _lastDownTick <= GetDoubleClickTime()
            && Math.Abs(_pos.X - _lastDownPos.X) <= GetSystemMetrics(SM_CXDOUBLECLK) / 2
            && Math.Abs(_pos.Y - _lastDownPos.Y) <= GetSystemMetrics(SM_CYDOUBLECLK) / 2
            && (GetClassLongPtr(hwnd, GCL_STYLE).ToInt64() & CS_DBLCLKS) != 0;

        // A third click starts a fresh pair, like real input.
        _lastDownMsg = isDouble ? 0 : msg;
        _lastDownTick = now;
        _lastDownPos = _pos;

        if (!isDouble) return msg;
        return msg switch
        {
            WM_LBUTTONDOWN => WM_LBUTTONDBLCLK,
            WM_RBUTTONDOWN => WM_RBUTTONDBLCLK,
            _ => WM_MBUTTONDBLCLK,
        };
    }

    void RebuildScreenMenu()
    {
        _screenMenu.DropDownItems.Clear();
        foreach (var (screen, i) in Screen.AllScreens.Select((s, i) => (s, i)))
        {
            var label = $"Display {i + 1}: {screen.Bounds.Width}x{screen.Bounds.Height}{(screen.Primary ? " (primary)" : "")}";
            var item = new ToolStripMenuItem(label) { Checked = screen.DeviceName == _target.DeviceName };
            item.Click += (_, _) => { _target = screen; _pos = Center(screen.Bounds); if (_active) _overlay.MoveTipTo(_pos); };
            _screenMenu.DropDownItems.Add(item);
        }
    }

    void OnDisplaysChanged(object? sender, EventArgs e)
    {
        _target = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == _target.DeviceName) ?? DefaultTarget();
        _pos = Center(_target.Bounds);
        if (_active) _overlay.MoveTipTo(_pos);
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
        _hook.Dispose();
        _restoreTimer.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _overlay.Dispose();
        base.ExitThreadCore();
    }
}
