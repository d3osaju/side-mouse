using System.Drawing.Drawing2D;
using static SideMouse.Native;

namespace SideMouse;

/// <summary>
/// A fake arrow cursor drawn on the second monitor. It never takes focus and is
/// click-through, so the fullscreen game keeps foreground and doesn't minimise.
/// Also doubles as the message window for the global hotkey.
/// </summary>
sealed class CursorOverlay : Form
{
    const int HotkeyId = 1;
    static readonly Point[] Arrow =
    [
        new(0, 0), new(0, 17), new(4, 13), new(7, 20), new(10, 19), new(7, 12), new(12, 12),
    ];

    public event Action? HotkeyPressed;

    public CursorOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        Size = new Size(26, 42);
        DoubleBuffered = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_LAYERED;
            return cp;
        }
    }

    public bool RegisterHotkey(uint mods, Keys key)
    {
        UnregisterHotkey();
        return Native.RegisterHotKey(Handle, HotkeyId, mods | MOD_NOREPEAT, (uint)key);
    }

    public void UnregisterHotkey() => UnregisterHotKey(Handle, HotkeyId);

    /// <summary>Places the arrow tip at a screen point without activating or re-ordering focus.</summary>
    public void MoveTipTo(Point p) =>
        SetWindowPos(Handle, HWND_TOPMOST, p.X, p.Y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.None; // anti-aliasing would bleed into the colour key
        using var path = new GraphicsPath();
        path.AddPolygon(Arrow.Select(p => new Point(p.X * 2, p.Y * 2)).ToArray());
        g.FillPath(Brushes.White, path);
        using var pen = new Pen(Color.Black, 2);
        g.DrawPath(pen, path);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && (int)m.WParam == HotkeyId) HotkeyPressed?.Invoke();
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (IsHandleCreated) UnregisterHotKey(Handle, HotkeyId);
        base.Dispose(disposing);
    }
}
