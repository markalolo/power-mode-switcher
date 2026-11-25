# CLAUDE.md

Instructions for Claude Code working in this repo.

## Working style
- Explain what you changed and what to test in plain language. Short and concrete.
- No em-dashes in anything you write (code comments, docs, commit messages, chat).
- When something needs a manual test, give numbered steps and say exactly what should happen.

## The project
A small WPF widget (C#, .NET 10, `net10.0-windows`) that switches Windows Power Mode with a vertical slider. **PLAN.md is the design reference**: read it before changing behavior, and keep it up to date when the design changes.

Phases 0 to 9 are done (PLAN.md sections 8 and 8b), including the ARM64 builds. The repo is also cloned on the ARM laptop, so work may happen on either an x64 or an ARM64 PC; a plain `dotnet build` builds for the PC it runs on.

## Critical technical rules
- Power Mode is controlled through power **overlay schemes** in `powrprof.dll` (`PowerSetActiveOverlayScheme`, `PowerGetActualOverlayScheme`, `PowerRegisterForEffectivePowerModeNotifications`). **Never use `powercfg /setactive`** to switch modes. That changes the power plan, which is the wrong layer on Modern Standby laptops.
- Keep native declarations in the `Native/` folder, and keep the power calls in `Services/PowerModeService.cs`.
- Never assume the development laptop's power mode GUIDs or labels apply to every PC. There are two mode sets (Standard and Classic, PLAN.md section 3); the same GUID can sit at a different stop in each. From Phase 8, labels and stops come from the current mode set.
- The app must never require administrator rights.
- The widget must stay at the bottom of the z-order and never steal focus (see PLAN.md section 4).
- Keep idle resource use minimal. No busy loops. The only timer is the 5-second safety poll.
- Clean up on exit: unregister power notifications, dispose the tray icon.

## Coding standards (from code review)
- **JSON:** always use System.Text.Json **source generation** (`JsonSerializerContext` with `[JsonSerializable]`). Never call reflection-based `JsonSerializer` overloads. Any new type saved to disk gets added to the context.
- **Windows APIs:** use **CsWin32** (`NativeMethods.txt`) for documented Win32 functions, structs and constants. Hand-write only what CsWin32 cannot generate (the undocumented overlay functions), using `[LibraryImport]`, never `[DllImport]`, with a comment saying why.
- **No trimming:** do not enable `PublishTrimmed`. It is not supported for WPF.
- Keep code tidy, with short comments on anything non-obvious. A professional developer may review changes.

## Commands
- Build: `dotnet build`
- Run: `dotnet run --project src/PowerModeSwitcher`
- Publish (small): `dotnet publish src/PowerModeSwitcher -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish`
- Publish (standalone, from Phase 7): `dotnet publish src/PowerModeSwitcher -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish-standalone`. No `EnableCompressionInSingleFile`: the owner chose a larger file over about 75 MB more memory use (see PLAN.md Phase 7 notes).
- ARM64 (from Phase 9): the same two commands with `-r win-arm64`. Either architecture can be published on either kind of PC.
- Before asking for a manual test, make sure the build succeeds with no warnings you introduced.
- Stop any running copy of the app before rebuilding (the .exe is locked while running). Stop it cleanly with `PowerModeSwitcher.exe --exit`; force-killing it leaves a stale tray icon.

## Git
- Commit at stable points with a clear one-line message, and push to `origin main`.
- Releases: push a tag like `v1.2.3`; `.github/workflows/release.yml` builds the .exe and creates the GitHub Release.
- Never commit `bin/`, `obj/`, `publish/`, or settings files.
- This is a public repo. Never put personal information (email addresses, names, machine paths) in files or commit messages. Commits use the GitHub no-reply email address.

## Dependencies
- Prefer the built-in .NET and Windows APIs. Only add a NuGet package if it clearly saves work, and explain what it is and why.
