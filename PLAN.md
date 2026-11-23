# Power Mode Switcher: Project Plan

A tiny Windows 11 desktop widget with a three-position slider that switches the Windows Power Mode between **Best power efficiency**, **Balanced**, and **Best performance**. It sits on the desktop (below all app windows), starts with Windows, and stays in sync if the mode is changed anywhere else.

Built with Claude Code. Developed and tested on a Windows 11 laptop with Modern Standby.

---

## 1. Goals and non-goals

### Goals
- Small floating widget with a 3-stop slider: Battery / Balanced / Performance.
- Widget lives at desktop level: below normal application windows, above the wallpaper and desktop icons.
- Starts automatically when the user signs in to Windows.
- Slider updates itself when Power Mode changes outside the app (Windows Settings, the battery flyout, plugging in or unplugging, Battery Saver, and so on).
- Very low resource use while idle.
- Tray icon with a small menu for settings and exit.

### Non-goals (for now)
- Global hotkey.
- Code signing. (Expect a one-time SmartScreen "Run anyway" prompt.)
- Switching classic power plans with `powercfg`. See section 3.
- Installer. A single .exe in a folder is enough.

---

## 2. Technology choices

| Item | Choice |
|---|---|
| Language | C# |
| Runtime | .NET 10 (LTS) |
| UI framework | WPF |
| Target | `net10.0-windows`, x64 |
| Windows API access | P/Invoke (`DllImport` / `LibraryImport`) into `powrprof.dll` and `user32.dll` |
| Settings storage | JSON file in `%APPDATA%\PowerModeSwitcher\settings.json` |
| Autostart | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` registry value (no admin needed) |
| Tray icon | WinForms `NotifyIcon` (`UseWindowsForms` alongside WPF; no NuGet packages) |
| Build | `dotnet` CLI only. No Visual Studio required. VS Code is used for viewing files. |
| Release build | GitHub Actions on `windows-latest`, triggered by a version tag |

Publish as **framework-dependent, single-file** (`-p:PublishSingleFile=true --self-contained false`). It needs the .NET 10 Desktop Runtime (included with the .NET 10 SDK), so the .exe stays very small.

---

## 3. How Windows Power Mode actually works (read this first)

On Modern Standby laptops, Windows 11 usually exposes only the **Balanced** power plan. The Settings page "Power mode" dropdown is a separate layer called a **power overlay scheme**. This app controls the overlay, not the plan.

**Do not use `powercfg /setactive` for switching.** It changes the plan, not the Power Mode, and has no visible effect on this machine.

### Overlay GUIDs

| Slider position | Settings label | Overlay GUID |
|---|---|---|
| Left | Best power efficiency | `961cc777-2547-4f9d-8174-7d86181b8a7a` |
| Middle | Balanced | `00000000-0000-0000-0000-000000000000` |
| Right | Best performance | `ded574b5-45a0-4f42-8737-46345c09c238` |

Also recognize `3af9b8d9-7c97-431d-ad78-34a8bfea439f` (older "Better battery" overlay) when reading the current mode, and map it to the Left position. If an unknown GUID is read, show the slider in a neutral "unknown" state rather than guessing.

### API calls (all in `powrprof.dll`)

| Purpose | Function | Notes |
|---|---|---|
| Set mode | `PowerSetActiveOverlayScheme(Guid)` | Undocumented but widely used and stable. Returns 0 on success. |
| Read mode | `PowerGetActualOverlayScheme(out Guid)` | Undocumented. Use as the source of truth for the slider position. |
| Watch for changes | `PowerRegisterForEffectivePowerModeNotifications` | **Documented** (Windows 10 1809+). Fires when the effective mode changes. Use it as a trigger, then re-read with `PowerGetActualOverlayScheme`. Unregister on exit. |

Wrap all of these in one class (`PowerModeService`) so the undocumented parts sit in one place and can be swapped if Windows ever changes them.

### Things that also change the mode (the slider must follow them)
- **Plugged in vs on battery.** Windows stores a separate Power Mode for each. When the power source changes, the effective mode can jump. Listen for `SystemEvents.PowerModeChanged` (or `WM_POWERBROADCAST`) and re-read.
- **Battery Saver** turning on or off.
- **Energy Saver** (Windows 11 24H2+; it replaced Battery Saver and can be on while plugged in). Tested 2026-11-22: turning it on sends `GUID_ENERGY_SAVER_STATUS` = 1 and drops the *effective* overlay to best power efficiency, but the *actual* overlay (the user's chosen mode) does not change and the old Battery Saver flag (`SYSTEM_POWER_STATUS.SystemStatusFlag`) stays 0. So the slider stays on the chosen mode, and the widget and tray tooltip say "Energy Saver on". The app listens with `PowerSettingRegisterNotification(GUID_ENERGY_SAVER_STATUS)`.
- Changes from Windows Settings or the battery flyout.
- PC maker utilities (for example Dell Optimizer or Lenovo Vantage) may adjust thermal behavior on their own. It does not normally change the Windows overlay, but note it if testing shows odd results.

### Fallback
Add a simple poll every 5 seconds as a safety net in case a notification is missed. It costs almost nothing. Only update the UI if the value actually changed.

---

## 4. The widget window

### Look (matches the original design mockup)
- **Vertical** widget, about 230 x 340 pixels at 100% scaling.
- Panel: corner radius 20, background navy `#0F2F4C` at about 85% opacity, 1 px border in white at 14% opacity, 20 px padding.
- Left side: a vertical track 88 x 300, fully rounded (radius 44), white at 8% opacity, with a small 6 px dot at each of the three stops.
- The knob is an 80 x 80 round image that sits on the track and **changes picture by position**:

| Position | Mode | Knob image |
|---|---|---|
| Top | Best performance | `knob-performance-rocket.svg` (rocket with flames, red) |
| Middle | Balanced | `knob-balanced-sailboat.svg` (sailboat, teal) |
| Bottom | Best power efficiency | `knob-battery-ice.svg` (ice cube, blue) |

- Knob centers sit at 50, 150 and 250 px down the track. The knob has a soft drop shadow.
- When the mode changes, the knob slides to its new spot over about 200 ms with an ease-out curve (no bounce), and its image swaps.
- Right side: labels "Performance", "Balanced", "Battery", each vertically centered on its stop. The current one is white, 15 px, semibold; the others are white at about 62% opacity, 13 px, medium.
- Font: Inter if installed, otherwise Segoe UI.
- Clicking a stop, a label, or dragging the knob and releasing near a stop all change the mode.
- Optional small text line under the widget: "On battery" / "Plugged in".
- Crisp at any display scaling (per-monitor DPI aware, V2).

### Images
- The three SVGs above live in `src/PowerModeSwitcher/Assets/` and are the source images. WPF cannot show SVG directly, so they were hand-converted to WPF vector drawings in `Assets/KnobImages.xaml` (no package needed, sharp at any scaling). Keep both in sync.
- Tray icons: three .ico files made from the same drawings (16 to 48 px for the tray, plus 64 and 256 px for the .exe icon), so the tray icon also shows ice cube, sailboat or rocket. Regenerate them with `tools/make-icons.ps1`.

### Behavior
- Click or drag the slider to change mode. The change applies immediately.
- If setting the mode fails, snap the slider back to the real current value and show a brief tooltip with the error.
- Drag the widget by its background to reposition it. Save the position to settings and restore it at next launch.
- "Lock position" option in the tray menu to prevent accidental drags.
- If the saved position is off-screen (monitor unplugged), move the widget to the bottom-right of the primary screen.
- **Show mode names** (added 2026-11-22): menu switch, on by default. When off, the labels are hidden and the widget narrows to just the track (128 px wide at full size).
- **Half-size widget** (added 2026-11-22): menu switch, off by default. Scales the whole widget to 50%. When the size changes, the corner nearest the screen edge stays in place.

### Desktop-level z-order (the tricky part)
The widget should stay **below** normal windows and never pop in front of them.

Approach, in order:
1. Window style: no border, no taskbar button (`ShowInTaskbar = false`), add `WS_EX_TOOLWINDOW` so it does not appear in Alt+Tab, and `WS_EX_NOACTIVATE` so clicking it does not steal focus from other apps.
2. On startup, place it at the bottom of the z-order with `SetWindowPos(hwnd, HWND_BOTTOM, ..., SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE)`.
3. Hook `WM_WINDOWPOSCHANGING` and force `hwndInsertAfter = HWND_BOTTOM` every time, so nothing can bring it to the front (including clicking on it).

**Known issue to test:** pressing **Win+D** (Show Desktop) may hide the widget along with everything else. To make it survive Win+D, a stretch option is to parent the window into the desktop shell (the `Progman` / `WorkerW` window). This is more fragile and changed in recent Windows 11 builds, so do it only after the basic version works, behind a setting, and test carefully.

**Phase 3 result (2026-11-22):** Win+D hides the widget. Pressing Win+D again brings it back. Accepted for now; surviving Win+D stays in "Optional later".

---

## 5. Tray icon

- Icon reflects the current mode: ice cube, sailboat or rocket (see section 4, Images).
- Tooltip: "Power mode: Balanced (plugged in)".
- Right-click menu (the same menu also opens when the widget is right-clicked; it follows the Windows light/dark setting):
  - Version header (greyed out)
  - Best power efficiency / Balanced / Best performance (radio-style, current one checked)
  - Show widget / Hide widget
  - Lock widget position
  - Show mode names (checkbox)
  - Half-size widget (checkbox)
  - Start with Windows (checkbox)
  - Exit
- Double-click toggles widget visibility.

---

## 6. Startup and settings

- **Start with Windows** on by default. Writes `PowerModeSwitcher` to `HKCU\...\Run` pointing at the current .exe path. If the .exe is moved, the next launch rewrites the path.
- **Single instance:** use a named mutex. A second launch just shows the existing widget and exits.
- `settings.json` fields: `WindowLeft`, `WindowTop`, `PositionLocked`, `WidgetVisible`, `ShowModeNames`, `HalfSize`, `StartWithWindows`.
- Should not require administrator rights. Verify in phase 1.

---

## 7. Project layout

```
power-mode-switcher/
  CLAUDE.md                       instructions for Claude Code
  PLAN.md                         this design document
  README.md
  LICENSE                         MIT
  .gitignore                      (standard .NET / VS Code)
  PowerModeSwitcher.sln
  src/PowerModeSwitcher/
    PowerModeSwitcher.csproj
    app.manifest                  per-monitor DPI aware, never asks for admin
    App.xaml / App.xaml.cs        startup, single instance, --exit, wiring
    WidgetWindow.xaml / .cs       the floating widget
    TrayIcon.cs                   tray icon and the shared right-click menu
    MenuTheme.cs                  light/dark menu colors and drawing
    Services/PowerModeService.cs  power mode, Energy Saver, change events
    Services/DesktopZOrder.cs     HWND_BOTTOM / window style helpers
    Services/ScreenPlacement.cs   position, off-screen recovery, resizing
    Services/SettingsService.cs   JSON load/save
    Services/StartupService.cs    Run-key autostart
    Native/NativeMethods.cs       P/Invoke declarations
    Assets/                       knob SVGs, KnobImages.xaml, tray .ico files
  tools/make-icons.ps1            regenerates the .ico files
  .github/workflows/release.yml   builds the .exe and creates a release on a version tag
```

---

## 8. Build phases

Each phase ends with something that can be run and checked by hand. Commit after each phase.

### Phase 0: Repo and tooling
- Create the GitHub repo `power-mode-switcher` and clone it (see section 10).
- Claude Code adds `PLAN.md`, `CLAUDE.md`, `.gitignore`, and an empty WPF project that builds and opens a blank window.
- **Check:** `dotnet run` shows a window.

### Phase 1: Prove the power API (no UI yet)
- Build `PowerModeService`. Add a temporary console test or debug buttons that read the current mode and set each of the three modes.
- **Check:** change the mode with the app, then open Settings > System > Power & battery and confirms it matches. Then the reverse. Confirm no admin prompt.

### Phase 2: Widget with working slider
- Build the widget UI and wire the slider to the service.
- Subscribe to change notifications, AC/DC changes, and the 5-second poll.
- **Check:** slider changes mode; changing mode in Settings moves the slider within a second or two; plugging in or unplugging updates it.

### Phase 3: Desktop-level behavior
- Borderless, no taskbar button, not in Alt+Tab, no focus stealing, pinned to bottom of z-order.
- Drag to move, save position, lock position.
- **Check:** open Chrome full screen over it; click the widget's visible area when uncovered; it never jumps in front of apps. Test Win+D and record what happens.

### Phase 4: Tray, autostart, single instance
- Tray icon, menu, icons per mode.
- Start with Windows toggle; single-instance mutex.
- **Check:** reboot; widget appears in the saved spot with the correct mode. Launching the .exe twice does not create two widgets.

### Phase 5: Polish and release
- Light/dark theme, DPI checks at 100%, 150%, 200%, multi-monitor position recovery.
- `README.md` with plain-language instructions.
- GitHub Actions `release.yml`: on a tag like `v1.0.0`, build on `windows-latest`, publish single-file, attach the .exe to a GitHub Release.
- **Check:** download the .exe from the GitHub Release page, put it in a permanent folder (for example `C:\Tools\PowerModeSwitcher\`), run it, accept SmartScreen once.

### Status (2026-11-22)
- Phases 0 to 5 are done. The current release is v1.0.1.
- Added along the way: menu switches for "Show mode names" and "Half-size widget"; one shared light/dark menu for the tray icon and the widget; `--exit` to close a running copy cleanly.
- Development (Debug) builds do not update Start with Windows; only release builds do.
- Not yet confirmed in daily use: reboot autostart with the installed copy, sleep/resume (see section 9). Energy Saver detection was added after v1.0.0 (see section 3).

### Optional later
- Survive Win+D by attaching to the desktop shell (section 4).
- Code signing.
- Show estimated battery time or charge percentage on the widget.

---

## 9. Testing checklist

- [ ] Slider sets each of the three modes; Settings page agrees.
- [ ] Changing mode in Settings updates the slider.
- [ ] Changing mode from the battery flyout updates the slider.
- [ ] Plug in / unplug updates the slider to that source's saved mode.
- [ ] Energy Saver (Battery Saver) on/off is reflected.
- [ ] Widget stays below every app window, including after clicking it.
- [ ] Widget not in taskbar or Alt+Tab.
- [ ] Position persists across restarts; recovers if off-screen.
- [ ] Starts with Windows after reboot.
- [ ] Only one instance runs.
- [ ] Idle CPU near 0%, memory reasonable (check in Task Manager).
- [ ] Works after sleep and resume.
- [ ] No admin prompt at any point.

---

## 10. Development setup

1. **Install the .NET 10 SDK** (x64) from Microsoft's .NET download page.
2. **Install Git** (Git for Windows). In a terminal, `git --version` should print a version.
3. **Clone the repo** and open a terminal in its folder.
4. **Build and run:** `dotnet run --project src/PowerModeSwitcher`. No Visual Studio needed.
5. **Optional:** start Claude Code in the folder. It reads `CLAUDE.md` for the project rules.
