# Power mode and power plan GUIDs: reference

Compiled November 2026 from Microsoft documentation, PC maker manuals and open-source tools. Use this as the source for the app's built-in table (PLAN.md, Phase 8).

## Summary

- **There is no long list of vendor-specific power mode GUIDs.** Windows only supports a small, fixed set of power modes. Microsoft's documentation for the Windows 11 power mode API says the three default modes cannot be removed or changed and are the only supported values. PC makers (Dell, Lenovo, ASUS, Microsoft Surface and others) do not add their own power mode GUIDs. Their utilities map their own mode names onto the same Windows GUIDs.
- What differs between machines is the **labels** Windows shows, the **number of stops** (Windows 10 had four positions on battery), and which of the **two "middle" GUIDs** the machine uses.
- **Power plans are a different layer** (`powercfg /setactive`). The app never switches plans, but it should notice when the active plan is not Balanced, because Windows only offers Power Mode on the Balanced plan or a plan derived from it.

## 1. Power mode (overlay) GUIDs

These are all the power mode GUIDs found anywhere. Order is from most battery saving (bottom of the widget) to most performance (top).

| Order | GUID | Windows 11 Settings label | Windows 10 slider label | Internal name (registry, Snapdragon 25H2) | Notes |
|---|---|---|---|---|---|
| 1 | `961cc777-2547-4f9d-8174-7d86181b8a7a` | Best power efficiency | Better battery (shown as **Recommended** on some devices, including Surface) | Better Battery-life Overlay | Documented as `GUID_POWER_MODE_BEST_EFFICIENCY`. |
| 2 | `00000000-0000-0000-0000-000000000000` | Balanced | (see note) | No overlay | Documented as `GUID_POWER_MODE_NONE`. Means "use the Balanced plan as is". Many Windows 10 scripts call this position "Better performance". |
| 3 | `3af9b8d9-7c97-431d-ad78-34a8bfea439f` | Better Performance, **on Classic PCs only** (see PLAN.md section 3, "Mode sets"; seen on a Surface Laptop 2026-11-25). Not offered on Standard PCs. | Better performance (the Windows 10 default) | High Performance Overlay | Microsoft's OEM docs list it as the "Better Performance" slider mode. Not in the documented Windows 11 list. |
| 4 | `ded574b5-45a0-4f42-8737-46345c09c238` | Best performance | Best performance | Max Performance Overlay | Documented as `GUID_POWER_MODE_BEST_PERFORMANCE`. |

**Order 2 vs 3 is an inference.** Microsoft never ranks them against each other. The internal name "High Performance Overlay" for `3af9b8d9`, and Windows 10 placing "Better performance" just below "Best performance", both put it above plain Balanced.

**Windows 10's leftmost "Battery saver" slider position is not a GUID.** It turns on Battery Saver, which is a separate state. On Windows 11 24H2 and later, this is Energy Saver, which the app already handles separately.

### Bug this exposes in the current code

`PowerModeService.cs` treats `3af9b8d9` as a "legacy better battery" overlay and shows it at the **bottom** (best power efficiency). Microsoft's OEM documentation says it is **"Better performance"**, a middle-to-high setting. A machine using it would show the knob on the ice cube while actually running in a performance-leaning mode. This may be part of what looked wrong on the Surface.

**Status: fixed 2026-11-24, then extended in Phase 8 (2026-11-25).** On 2026-11-24 the app started naming `3af9b8d9` "Better performance" instead of putting it on the bottom stop. Phase 8 added the two mode sets: on Classic PCs (Recommended / Better performance / Best performance) it is the middle stop with the sailboat, and on Standard PCs, which don't offer it, the widget shows its neutral state and names it.

## 2. How PC makers label the same modes

PC maker utilities and manuals use their own names but drive the same Windows GUIDs. Useful for the Learn wizard's suggestions and the README. Windows Settings itself still shows the Windows labels.

| Windows mode | Lenovo ThinkPad (Intelligent Cooling) | Lenovo mobile workstation | Dell Optimizer thermal modes (user report) | ASUS (G-Helper) |
|---|---|---|---|---|
| Battery saver (Windows 10, battery only) | Eco | Eco | n/a | n/a |
| Better battery / Best power efficiency (`961cc777`) | Eco | Balanced | Quiet, Cool | Silent |
| Balanced (`00000000`) | n/a in Lenovo's table | n/a | Optimized | Balanced |
| Better performance (`3af9b8d9`) | Balanced | Performance | n/a | n/a |
| Best performance (`ded574b5`) | Performance | Ultra Performance | Ultra Performance | Turbo |

- **Lenovo:** on ThinkPads, Intelligent Cooling *is* the Windows power slider. Lenovo's thermal overview maps the four Windows 10 slider positions to its modes. Auto mode (Fn+T) can change things on its own.
- **Dell:** Dell Optimizer has a setting to keep its thermal mode in sync with Windows Power Mode in both directions. The mapping above is from a Dell community post, not Dell documentation.
- **ASUS:** G-Helper (a popular Armoury Crate replacement) sets exactly the three Windows 11 GUIDs.
- **HP and Acer:** HP's OMEN and Smart Sense modes and Acer's NitroSense and PredatorSense modes are separate from Windows Power Mode. They run alongside it, and changing one does not move the other. No extra GUIDs.
- **AMD:** some Ryzen laptops add an "AMD Power Slider" setting inside the power plan. It is not a Windows power mode and does not add GUIDs the app can set.

**What this means for the app:** other utilities may change the Windows mode themselves (Dell sync, Lenovo auto mode, G-Helper). The widget already follows outside changes, so this works. It is worth a line in the README.

## 3. Power plan GUIDs (different layer, for diagnostics only)

| Plan | GUID | Notes |
|---|---|---|
| Balanced | `381b4222-f694-41f0-9685-ff5bb260df2e` | The only built-in plan on most Modern Standby laptops. Power Mode only works on this plan or one derived from it. |
| High performance | `8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c` | |
| Power saver | `a1841308-3541-4fab-bc81-f71556f20b4a` | |
| Ultimate Performance | `e9a42b02-d5df-448d-aa00-03f14749eb61` | Hidden by default except on Pro for Workstations. Not for battery-powered systems. |

**PC maker plans** (for example Acer's "acer" plan, ASUS Armoury Crate plans, the AMD Ryzen Balanced plan) get **different GUIDs on each install**, so they cannot be baked in. If the app ever needs them, it should list plans at run time (`PowerEnumerate` + `PowerReadFriendlyName`) instead.

**Watch out for wrong information online.** Several popular articles list `381b4222-...` (the Balanced *plan*) as the "Better performance" *slider* GUID, or tell readers to use `powercfg /setactive` with power mode GUIDs. Both mix up the plan and power mode layers.

## 4. Engineering findings that matter more than the GUID list

### 4a. Windows 11 now has a documented power mode API

Microsoft documents these functions in `powrprof.h` (minimum: Windows 11). They read and set the user's chosen mode **separately for plugged in and on battery**:

- `PowerGetUserConfiguredACPowerMode(GUID*)` / `PowerSetUserConfiguredACPowerMode(const GUID*)`
- `PowerGetUserConfiguredDCPowerMode(GUID*)` / `PowerSetUserConfiguredDCPowerMode(const GUID*)`

The docs note that power modes "were previously referred to as overlays." This answers the code review question about whether a documented API exists: one does, for Windows 11.

Suggested approach (Phase 6):
- Prefer the documented functions where they exist. Through CsWin32 if its metadata includes them; otherwise hand-written `[LibraryImport]` with a comment, since they are still documented.
- Keep the undocumented overlay functions as the fallback, and for reading `3af9b8d9`, which the documented API does not list.
- Bonus: the AC/DC functions let the app show or set the plugged-in and on-battery modes directly, which helps the Learn wizard.
- Test on the development laptop that set and get through the documented API match Windows Settings exactly before switching over.

**Status: done 2026-11-24.** CsWin32's metadata includes all four functions and the `GUID_POWER_MODE_*` constants. On the development laptop (Windows 11 build 26200) they returned the same values as the overlay functions for both power sources, re-setting the current value was accepted with no change, and an invalid GUID was rejected (error 87). The app now uses them first, with the overlay functions as the fallback.

### 4b. ARM64 crash: the current declaration passes the GUID the wrong way

`PowerSetActiveOverlayScheme` takes a **pointer** to a GUID. The current `NativeMethods.cs` declares it as `PowerSetActiveOverlayScheme(Guid overlaySchemeGuid)` (by value). On x64 this happens to work, because .NET passes a 16-byte struct by hidden reference. On ARM64 the GUID's bytes are passed in registers and read as an address. A Snapdragon developer reports that setting Balanced fails with error 87 and setting Best power efficiency **crashes the app silently** (access violation).

Fix: declare it as `in Guid` or `ref Guid` (pointer). The `Get` functions with `out Guid` are not affected. Do this in Phase 6, not Phase 9, so the ARM build starts from correct code. The same rule applies to the new documented `Set` functions, which also take `const GUID*`.

**Status: fixed 2026-11-24.** The declaration (now in `Native/PowerOverlayApi.cs`) is `in Guid`, and the generated marshalling passes a `Guid*`. CsWin32 generates the documented `Set` functions with `in Guid` as well.

### 4c. Other checks worth adding to Copy diagnostics (Phase 8)

- Active power plan GUID and name. Warn if it is not Balanced, because Power Mode only applies on Balanced-derived plans.
- Plugged-in and on-battery user modes (from 4a), not just the current one.
- Effective power mode (the `EFFECTIVE_POWER_MODE` value from the existing notification), which shows when Energy Saver, Battery Saver or Game Mode is overriding the user's choice.

## 5. Reference readouts (Copy diagnostics)

Output of the app's Copy diagnostics on real machines, for comparing the two mode sets. User names, machine names and serial numbers are never included.

### Standard: Dell XPS 16 9640 (development laptop), 2026-11-25

```
App:      version 1.0.0, X64 process   (local development build)
Windows:  Windows 11 Pro, version 25H2 (build 26200.9448), X64
Device:   Dell Inc. XPS 16 9640

Power source:   plugged in
Energy Saver:   off
Mode set:       Standard (Best power efficiency / Balanced / Best performance), not decided yet, so using Standard
Widget shows:   Best performance (top stop)

Chosen power mode, plugged in:
  documented API: ded574b5-45a0-4f42-8737-46345c09c238 (Max Performance Overlay)
  registry:       ded574b5-45a0-4f42-8737-46345c09c238 (Max Performance Overlay)
Chosen power mode, on battery:
  documented API: 00000000-0000-0000-0000-000000000000 (No overlay)
  registry:       00000000-0000-0000-0000-000000000000 (No overlay)
Actual overlay:     ded574b5-45a0-4f42-8737-46345c09c238 (Max Performance Overlay)
Effective overlay:  ded574b5-45a0-4f42-8737-46345c09c238 (Max Performance Overlay)
Effective power mode: 4 (Max Performance)

Active power plan:  381b4222-f694-41f0-9685-ff5bb260df2e Balanced
Plan personality:   381b4222-f694-41f0-9685-ff5bb260df2e (Balanced)
All power plans:
  381b4222-f694-41f0-9685-ff5bb260df2e Balanced
Subkeys under HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes:
  381b4222-f694-41f0-9685-ff5bb260df2e Balanced
  3af9b8d9-7c97-431d-ad78-34a8bfea439f High Performance Overlay
  8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c High performance
  961cc777-2547-4f9d-8174-7d86181b8a7a Better Battery-life Overlay
  a1841308-3541-4fab-bc81-f71556f20b4a Power saver
  ded574b5-45a0-4f42-8737-46345c09c238 Max Performance Overlay
  e9a42b02-d5df-448d-aa00-03f14749eb61 Ultimate Performance
```

Notes: only Balanced is listed as a plan (Modern Standby hides the others, though their subkeys exist). The `3af9b8d9` subkey exists even on this Standard machine, so the presence of a subkey is not a signal for the mode set here.

### Classic: Microsoft Surface Laptop Go, 2026-11-25

```
App:      version 0.0.0, X64 process   (GitHub Actions test build)
Windows:  Windows 11 Pro, version 25H2 (build 26200.9974), X64
Device:   Microsoft Corporation Surface Laptop Go

Power source:   plugged in
Energy Saver:   off
Mode set:       Classic (Recommended / Better performance / Best performance), chosen in the one-time question
  Setting: Classic; detected: none; question shown: yes
  Last set by this app: plugged in ded574b5-45a0-4f42-8737-46345c09c238, on battery none
Widget shows:   Best performance (top stop)

Chosen power mode, plugged in:
  documented API: ded574b5-45a0-4f42-8737-46345c09c238 (Max Performance Overlay)
  registry:       ded574b5-45a0-4f42-8737-46345c09c238 (Max Performance Overlay)
Chosen power mode, on battery:
  documented API: 00000000-0000-0000-0000-000000000000 (No overlay)
  registry:       00000000-0000-0000-0000-000000000000 (No overlay)
Actual overlay:     ded574b5-45a0-4f42-8737-46345c09c238 (Max Performance Overlay)
Effective overlay:  ded574b5-45a0-4f42-8737-46345c09c238 (Max Performance Overlay)
Effective power mode: 4 (Max Performance)

Active power plan:  381b4222-f694-41f0-9685-ff5bb260df2e Balanced
Plan personality:   381b4222-f694-41f0-9685-ff5bb260df2e (Balanced)
All power plans:
  381b4222-f694-41f0-9685-ff5bb260df2e Balanced
Subkeys under HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes:
  381b4222-f694-41f0-9685-ff5bb260df2e Balanced (recommended)
  3af9b8d9-7c97-431d-ad78-34a8bfea439f High Performance Overlay
  8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c High Performance
  961cc777-2547-4f9d-8174-7d86181b8a7a Better Battery-life Overlay
  a1841308-3541-4fab-bc81-f71556f20b4a Power Saver
  ded574b5-45a0-4f42-8737-46345c09c238 Max Performance Overlay
  e9a42b02-d5df-448d-aa00-03f14749eb61 Ultimate Performance
```

Notes: taken after the one-time question was answered with Classic, so detection had nothing left to do ("detected: none"). On this PC, Windows 11 Settings shows `00000000` as **Recommended** (confirmed in the Phase 8 check). So on a Windows 11 Classic PC, "Recommended" is plain Balanced, not `961cc777`; row 1's "Recommended" note is about the Windows 10 slider.

### Standard on ARM64: Dell Latitude 7455 (Snapdragon X Elite), 2026-11-25

```
App:      version 1.3.0, Arm64 process   (local Phase 9 build)
Windows:  Windows 11 Pro, version 25H2 (build 26200.8875), Arm64
Device:   Dell Inc. Latitude 7455

Power source:   plugged in
Energy Saver:   off
Mode set:       Standard (Best power efficiency / Balanced / Best performance), chosen in the one-time question
  Setting: Standard; detected: none; kept until detected: none; question shown: yes
  Last set by this app: plugged in ded574b5-45a0-4f42-8737-46345c09c238, on battery 00000000-0000-0000-0000-000000000000
Widget shows:   Best performance (top stop)

Chosen power mode, plugged in:
  documented API: ded574b5-45a0-4f42-8737-46345c09c238 (Max Performance Overlay)
  registry:       ded574b5-45a0-4f42-8737-46345c09c238 (Max Performance Overlay)
Chosen power mode, on battery:
  documented API: 00000000-0000-0000-0000-000000000000 (No overlay)
  registry:       00000000-0000-0000-0000-000000000000 (No overlay)
Actual overlay:     ded574b5-45a0-4f42-8737-46345c09c238 (Max Performance Overlay)
Effective overlay:  ded574b5-45a0-4f42-8737-46345c09c238 (Max Performance Overlay)
Effective power mode: 4 (Max Performance)

Active power plan:  381b4222-f694-41f0-9685-ff5bb260df2e Balanced
Plan personality:   381b4222-f694-41f0-9685-ff5bb260df2e (Balanced)
All power plans:
  381b4222-f694-41f0-9685-ff5bb260df2e Balanced
Subkeys under HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes:
  381b4222-f694-41f0-9685-ff5bb260df2e Balanced
  3af9b8d9-7c97-431d-ad78-34a8bfea439f High Performance Overlay
  8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c High performance
  961cc777-2547-4f9d-8174-7d86181b8a7a Better Battery-life Overlay
  a1841308-3541-4fab-bc81-f71556f20b4a Power saver
  ded574b5-45a0-4f42-8737-46345c09c238 Max Performance Overlay
  e9a42b02-d5df-448d-aa00-03f14749eb61 Ultimate Performance
```

Notes: the first Windows on ARM machine tested (Phase 9), running the native ARM64 build. Answered Standard in the one-time question, so this laptop shows Best power efficiency / Balanced / Best performance in Settings. Its power values match the XPS readout: the same plan, personality, seven subkeys and plan name wording, and the same modes for both power sources. Being ARM64 changes nothing the app reads.

### Comparing the readouts

The comparison below is between the two mode sets (XPS and Surface). The Snapdragon laptop, also Standard, matches the XPS, so it adds no set-telling signal either.

- Both machines have the same seven PowerSchemes subkeys, including `961cc777` and `3af9b8d9`, and both list only the Balanced plan, with the Balanced personality.
- With `ded574b5` plugged in and `00000000` on battery (modes both sets share), the documented API, registry and overlay values are identical on both.
- The only differences are the wording of plan names (Surface: "Balanced (recommended)", "High Performance", "Power Saver"; XPS: "Balanced", "High performance", "Power saver") and the manufacturer. Plan names come from the PC maker's Windows image, and going by manufacturer would be a guess (other Surface models, or other makers, may differ). Neither is a reliable sign of the mode set.
- **Conclusion (PLAN.md 8c):** nothing the app reads tells the two sets apart without a sighting. Auto keeps the sighting rule (`3af9b8d9` means Classic, `961cc777` means Standard) and the one-time question.

## Sources

- Microsoft: [PowerGetUserConfiguredACPowerMode](https://learn.microsoft.com/en-us/windows/win32/api/powrprof/nf-powrprof-powergetuserconfiguredacpowermode) (and the matching DC and Set pages)
- Microsoft: [Customize the Windows performance power slider](https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/customize-power-slider) (OEM guide: slider modes, default overlay GUIDs, Balanced-plan requirement)
- Microsoft: [Power Policy Settings](https://learn.microsoft.com/en-us/windows/win32/power/power-policy-settings) (built-in plan GUIDs)
- Microsoft: [EFFECTIVE_POWER_MODE](https://learn.microsoft.com/en-us/windows/win32/api/powersetting/ne-powersetting-effective_power_mode)
- Microsoft: [Best practice power settings for Surface devices](https://learn.microsoft.com/en-us/surface/maintain-optimal-power-settings-on-surface-devices) (Surface slider labels, including "Recommended")
- Lenovo: [ThinkPad thermal performance overview](https://support.lenovo.com/us/en/solutions/ht512798-thinkpad-thermal-performance-overview) and ThinkPad user guides (Intelligent Cooling uses the Windows slider)
- Dell: [Dell Optimizer 6.0 overview](https://www.dell.com/support/kbdoc/en-us/000273253) (sync with Windows power mode); Dell community thread on MyDell thermal modes vs Windows power mode
- ASUS: [G-Helper source and issues](https://github.com/seerge/g-helper) (uses the three Windows 11 GUIDs; issue #3202 shows `3af9b8d9` on a Windows 11 machine)
- [BatteryHUD-Snapdragon](https://github.com/Whatevser/BatteryHUD-Snapdragon) (ARM64 by-value GUID crash; internal overlay names on Snapdragon 25H2)
- [AaronKelley/PowerMode](https://github.com/AaronKelley/PowerMode) (command-line tool using the same GUIDs)
- Elevenforum: [Change Power Plan in Windows 11](https://www.elevenforum.com/t/change-power-plan-in-windows-11.1439/) (Ultimate Performance GUID, Modern Standby plan behavior)
