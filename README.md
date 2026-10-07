# SideMouse

Use your mouse on a second monitor while a fullscreen game stays focused on the main one, without minimising it.

Click the **middle mouse button** to switch the mouse to the side screen. Click it again, or press **Esc**, to give it back to the game. That Esc is not passed to the game, so it won't open the pause menu. While it's on the side screen:

- A fake cursor moves around the second monitor.
- Scrolling and left/right clicks go to the window under that cursor.
- The game gets no mouse input and keeps focus, so it never minimises.

To change the shortcut (side buttons, F8, Scroll Lock or Ctrl+Alt+\`), right-click the tray icon and open **Shortcut**. You can pick the target display in the same menu.

## Run

```bash
dotnet run -c Release
```

Requires Windows and the .NET 10 SDK.

## Limitations

- Scrolling works almost everywhere. Clicks are posted messages, so some apps ignore them, for example web content in Chromium browsers.
- It uses a global low-level mouse hook, which strict kernel anti-cheat (Vanguard and similar) may not tolerate.
- The keyboard still goes to the game.
