# CLAUDE.md

Instructions for Claude Code working in this repo.

## Working style
- Explain what you changed and what to test in plain language. Short and concrete.
- No em-dashes in anything you write (code comments, docs, commit messages, chat).
- When something needs a manual test, give numbered steps and say exactly what should happen.

## The project
A small WPF widget (C#, .NET 10, `net10.0-windows`, x64) that switches Windows 11 Power Mode with a 3-position slider. **PLAN.md is the design reference**: read it before changing behavior, and keep it up to date when the design changes.

## Critical technical rules
- Power Mode is controlled through power **overlay schemes** in `powrprof.dll` (`PowerSetActiveOverlayScheme`, `PowerGetActualOverlayScheme`, `PowerRegisterForEffectivePowerModeNotifications`). **Never use `powercfg /setactive`** to switch modes. That changes the power plan, which is the wrong layer on Modern Standby laptops.
- Keep every P/Invoke declaration in `Native/NativeMethods.cs`, and keep the power calls in `Services/PowerModeService.cs`.
- The app must never require administrator rights.
- The widget must stay at the bottom of the z-order and never steal focus (see PLAN.md section 4).
- Keep idle resource use minimal. No busy loops. The only timer is the 5-second safety poll.
- Clean up on exit: unregister power notifications, dispose the tray icon.

## Commands
- Build: `dotnet build`
- Run: `dotnet run --project src/PowerModeSwitcher`
- Publish: `dotnet publish src/PowerModeSwitcher -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish`
- Before asking for a manual test, make sure the build succeeds with no warnings you introduced.
- Stop any running copy of the app before rebuilding (the .exe is locked while running). Stop it cleanly with `PowerModeSwitcher.exe --exit`; force-killing it leaves a stale tray icon.

## Git
- Commit at stable points with a clear one-line message, and push to `origin main`.
- Releases: push a tag like `v1.2.3`; `.github/workflows/release.yml` builds the .exe and creates the GitHub Release.
- Never commit `bin/`, `obj/`, `publish/`, or settings files.
- This is a public repo. Never put personal information (email addresses, names, machine paths) in files or commit messages. Commits use the GitHub no-reply email address.

## Dependencies
- Prefer the built-in .NET and Windows APIs. Only add a NuGet package if it clearly saves work, and explain what it is and why.
