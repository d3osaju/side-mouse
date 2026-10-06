using static SideMouse.Native;

namespace SideMouse;

/// <summary>
/// A toggle shortcut: either a mouse side button (caught in the mouse hook) or a
/// keyboard hotkey (registered with Windows).
/// </summary>
sealed record Shortcut(string Name, int XButton = 0, bool Middle = false, uint Mods = 0, Keys Key = Keys.None)
{
    public const int XBUTTON1 = 1, XBUTTON2 = 2;

    public bool IsMouse => XButton != 0 || Middle;

    /// <summary>True when a hook event is this shortcut's button; <paramref name="down"/> says press vs release.</summary>
    public bool MatchesMouse(int msg, uint mouseData, out bool down)
    {
        down = msg is WM_MBUTTONDOWN or WM_XBUTTONDOWN;
        if (Middle) return msg is WM_MBUTTONDOWN or WM_MBUTTONUP;
        return XButton != 0 && msg is WM_XBUTTONDOWN or WM_XBUTTONUP && (int)(mouseData >> 16) == XButton;
    }

    public static readonly Shortcut[] Presets =
    [
        new("Middle click", Middle: true),
        new("Mouse side button (forward)", XButton: XBUTTON2),
        new("Mouse side button (back)", XButton: XBUTTON1),
        new("F8", Key: Keys.F8),
        new("Scroll Lock", Key: Keys.Scroll),
        new("Ctrl+Alt+`", Mods: MOD_CONTROL | MOD_ALT, Key: Keys.Oem3),
    ];

    static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SideMouse", "shortcut.txt");

    public static Shortcut Load()
    {
        try
        {
            var name = File.ReadAllText(SettingsFile).Trim();
            return Presets.FirstOrDefault(p => p.Name == name) ?? Presets[0];
        }
        catch { return Presets[0]; }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, Name);
        }
        catch { /* not worth failing over */ }
    }
}
