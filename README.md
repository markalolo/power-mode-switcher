# Power Mode Switcher

A small desktop widget for Windows 11 that switches **Power Mode** with a slider:

| Knob | Mode | Same as Windows Settings |
|---|---|---|
| Rocket (top) | Performance | Best performance |
| Sailboat (middle) | Balanced | Balanced |
| Ice cube (bottom) | Battery | Best power efficiency |

It sits on your desktop behind your other windows, starts with Windows, and stays in sync when the mode is changed anywhere else (Settings, the battery flyout, plugging in or unplugging).

## Requirements

- Windows 11 on an x64 PC. It's most useful on laptops, where Windows offers the three Power Mode choices.
- The [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0). If you have the .NET 10 SDK installed, you already have it.
- No administrator rights.

## Install

1. Open the [Releases page](../../releases) and download `PowerModeSwitcher.exe` from the newest release.
2. Make a permanent folder for it, for example `C:\Tools\PowerModeSwitcher\`, and move the .exe there. (If you move it later, just run it once from the new spot.)
3. Double-click it. The first time, Windows SmartScreen may say it "protected your PC" because the app isn't signed. Click **More info**, then **Run anyway**. You only need to do this once per download.
4. The widget appears in the bottom-right corner of your screen.

If Windows says .NET is missing, it offers a download link. Install the runtime and run the app again.

To update, right-click the tray icon, choose **Exit**, and replace the .exe with the one from the newest release.

## Using it

**The widget**
- Click a stop on the track, click a mode name, or drag the knob and let go near a stop.
- Drag the navy background to move the widget. It remembers where you put it.
- Right-click the widget for the menu.

**The tray icon** (near the clock)
- Shows the current mode as a rocket, sailboat or ice cube. Hover to see the mode and power source.
- Windows 11 hides new tray icons at first. Click the **^** arrow by the clock and drag the icon onto the taskbar to keep it visible.
- Double-click it to show or hide the widget. Right-click it for the menu.

**The menu** (same from the tray icon or the widget)
- **Best power efficiency / Balanced / Best performance**: switch mode.
- **Hide widget / Show widget**: the app keeps running in the tray while the widget is hidden.
- **Lock widget position**: stops accidental dragging.
- **Show mode names**: turn off to show just the track.
- **Half-size widget**: makes the widget half as big.
- **Start with Windows**: on by default.
- **Exit**.

The menu follows your Windows light or dark mode.

## Good to know

- **Win+D (Show Desktop)** hides the widget along with everything else. Press Win+D again to bring it back.
- Windows keeps a separate Power Mode for **plugged in** and **on battery**, so the slider can jump when you plug in or unplug. That's Windows switching, and the widget just follows.
- **Energy Saver** (Quick Settings, Win+A) makes Windows save as much power as it can, whatever mode you picked. While it's on, the line under the widget says "Energy Saver on" and the slider stays on your chosen mode, which comes back when Energy Saver is turned off.
- Running the .exe a second time doesn't open a second copy. It brings back the widget of the copy that's already running.
- Settings are saved in `%APPDATA%\PowerModeSwitcher\settings.json`. The app sends nothing over the network.
- To read and set Power Mode, the app uses two Windows functions that Microsoft hasn't documented. They're widely used and have been stable for years, but a future Windows update could change them.

## Uninstall

1. Right-click the tray icon, uncheck **Start with Windows**, then click **Exit**.
2. Delete the folder with the .exe (for example `C:\Tools\PowerModeSwitcher\`).
3. Optional: delete `%APPDATA%\PowerModeSwitcher` to remove the saved settings.

## For developers

- Built with C#, WPF and .NET 10 (`net10.0-windows`, x64). The only NuGet package is [CsWin32](https://github.com/microsoft/CsWin32), which generates the Windows API declarations at build time (nothing extra ships with the app). Settings use System.Text.Json source generation.
- Build: `dotnet build`
- Run: `dotnet run --project src/PowerModeSwitcher`
- Stop a running copy cleanly: `PowerModeSwitcher.exe --exit`
- Publish a single .exe: `dotnet publish src/PowerModeSwitcher -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish`
- Release: push a tag like `v1.0.0`. GitHub Actions builds the .exe and attaches it to a new release (`.github/workflows/release.yml`).
- Tray and app icons are made from the knob drawings by `tools/make-icons.ps1`.
- Power Mode is changed through Windows power overlay schemes (`powrprof.dll`), not `powercfg`. See `PLAN.md` for the design and `CLAUDE.md` for project rules.
- Built with [Claude Code](https://claude.com/claude-code).

## License

[MIT](LICENSE)
