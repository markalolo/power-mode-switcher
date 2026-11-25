# Power Mode Switcher

A small desktop widget for Windows 11 that switches **Power Mode** with a slider. Windows offers one of two sets of three power modes, depending on the PC, and the widget matches whichever yours shows:

| Knob | Most PCs (Standard) | Some PCs, such as Surface (Classic) |
|---|---|---|
| Rocket (top) | Best performance | Best performance |
| Sailboat (middle) | Balanced | Better performance |
| Ice cube (bottom) | Best power efficiency | Recommended |

It sits on your desktop behind your other windows, starts with Windows, and stays in sync when the mode is changed anywhere else (Settings, the battery flyout, plugging in or unplugging).

## Requirements

- Windows 11 on a 64-bit Intel or AMD PC. It's most useful on laptops, where Windows offers the Power Mode choices. (A version for ARM PCs, such as Snapdragon laptops, is planned.)
- No administrator rights.

## Which file do I download?

Each release has two files. They are the same app.

| File | Size | Use it if |
|---|---|---|
| `PowerModeSwitcher-x64-standalone.exe` | about 150 MB | **Not sure? Pick this one.** It runs on its own, with nothing else to install. |
| `PowerModeSwitcher-x64.exe` | under 1 MB | You already have the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (it comes with the .NET 10 SDK). If it's missing, Windows offers a download link when you start the app. |

Both use about the same memory while running. To check that your PC is Intel or AMD: **Settings > System > About**, where **System type** should say "x64-based processor".

## Install

1. Open the [Releases page](../../releases) and download one of the files from the newest release (see above).
2. Make a permanent folder for it, for example `C:\Tools\PowerModeSwitcher\`, and move the .exe there. (If you move it later, just run it once from the new spot.)
3. Double-click it. The first time, Windows SmartScreen may say it "protected your PC" because the app isn't signed. Click **More info**, then **Run anyway**. You only need to do this once per download.
4. The widget appears in the bottom-right corner of your screen.

**To update:** right-click the tray icon, choose **Exit**, put the new .exe in the same folder, delete the old one, and run the new one. Your settings stay, and Start with Windows switches to the new file by itself. (Releases before 1.1 had a single file called `PowerModeSwitcher.exe`; delete that one when you switch.)

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
- **The three power modes** (for example Best power efficiency / Balanced / Best performance): switch mode.
- **Power mode options**: which set of modes your PC's Windows Settings shows. **Automatic** (the default) works it out once you pick a mode in Windows Settings; if it can't tell yet, the app asks you once. You can also pick **Standard** or **Classic** yourself.
- **Hide widget / Show widget**: the app keeps running in the tray while the widget is hidden.
- **Lock widget position**: stops accidental dragging.
- **Show mode names**: turn off to show just the track.
- **Half-size widget**: makes the widget half as big.
- **Start with Windows**: on by default.
- **Copy diagnostics**: copies details about your PC's power settings (no personal information) for reporting a problem.
- **Exit**.

The menu follows your Windows light or dark mode.

## Good to know

- **Win+D (Show Desktop)** hides the widget along with everything else. Press Win+D again to bring it back.
- Windows keeps a separate Power Mode for **plugged in** and **on battery**, so the slider can jump when you plug in or unplug. That's Windows switching, and the widget just follows.
- **Energy Saver** (Quick Settings, Win+A) makes Windows save as much power as it can, whatever mode you picked. While it's on, the line under the widget says "Energy Saver on" and the slider stays on your chosen mode, which comes back when Energy Saver is turned off.
- **PC maker apps** can change the power mode too (for example Dell Optimizer's sync setting, Lenovo's auto mode, or G-Helper on ASUS). The widget follows those changes. HP and Acer modes run alongside Windows Power Mode without changing it.
- **Two sets of power modes.** Most PCs show Best power efficiency, Balanced and Best performance. Some, such as Surface laptops, show Recommended, Better performance and Best performance instead. If the widget ever shows labels that don't match Windows Settings, choose the right set under **Power mode options** in the menu.
- If Windows is using a mode the current set doesn't include, the knob disappears and the line under the widget names the mode. Picking any stop switches as usual.
- **"Power mode needs the Balanced power plan"** means Windows is on another power plan (for example High performance), where it ignores Power Mode. Switch back to Balanced in Control Panel > Power Options.
- Running the .exe a second time doesn't open a second copy. It brings back the widget of the copy that's already running.
- Settings are saved in `%APPDATA%\PowerModeSwitcher\settings.json`. The app sends nothing over the network.
- On Windows 11 the app reads and sets Power Mode with Microsoft's documented functions. For Better performance, and as a fallback on older Windows, it uses two older Windows functions that Microsoft hasn't documented. They're widely used and have been stable for years, but a future Windows update could change them.

## Uninstall

1. Right-click the tray icon, uncheck **Start with Windows**, then click **Exit**.
2. Delete the folder with the .exe (for example `C:\Tools\PowerModeSwitcher\`).
3. Optional: delete `%APPDATA%\PowerModeSwitcher` to remove the saved settings.

## For developers

- Built with C#, WPF and .NET 10 (`net10.0-windows`, x64). The only NuGet package is [CsWin32](https://github.com/microsoft/CsWin32), which generates the Windows API declarations at build time (nothing extra ships with the app). Settings use System.Text.Json source generation.
- Build: `dotnet build`
- Run: `dotnet run --project src/PowerModeSwitcher`
- Stop a running copy cleanly: `PowerModeSwitcher.exe --exit` (this closes whichever copy is running, whatever its file name).
- Write the diagnostics report to a file: `PowerModeSwitcher.exe --diagnostics [file]` (read only; safe alongside a running copy).
- Publish the small .exe: `dotnet publish src/PowerModeSwitcher -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish`
- Publish the standalone .exe: `dotnet publish src/PowerModeSwitcher -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish-standalone` (not compressed on purpose: compression would halve the file but add about 75 MB of memory use; trimming is not supported for WPF).
- Release: push a tag like `v1.0.0`. GitHub Actions builds the .exe and attaches it to a new release (`.github/workflows/release.yml`).
- Tray and app icons are made from the knob drawings by `tools/make-icons.ps1`.
- Power Mode is read and set with the documented Windows 11 functions (`PowerGet/SetUserConfiguredAC/DCPowerMode`), falling back to the undocumented power overlay functions (`powrprof.dll`). It never uses `powercfg`, which changes the power plan, a different layer. See [`docs/power-mode-guids.md`](docs/power-mode-guids.md) for every power mode GUID, `PLAN.md` for the design and `CLAUDE.md` for project rules.
- Built with [Claude Code](https://claude.com/claude-code).

## License

[MIT](LICENSE)
