# Windows Taskbar Group

A Windows utility that clusters taskbar icons into named group folders. Clicking a group's taskbar button opens a pop-up with its apps. Windows-only. The owner is Kurt.

## Layout

```
TaskbarGroup/   TaskbarGroup.exe: .NET 8 WinForms, x64, single-file framework-dependent (build.bat -> publish\). v0.2.0
```

## How it works (v0.1.0, click to expand)

Windows 11 has no supported API for custom taskbar items or flyouts, so:
- **One exe, two roles.** No arguments = the settings window (`SettingsForm`). `--group <id>` = that group's pop-up (`PopupForm`), which is what a pinned shortcut runs. `--edit <id>` opens settings at that group (the pop-up's ⚙). Only one settings window runs at a time (a second start brings it to the front).
- **Pinning:** Pin to Taskbar… writes `<Group name>.lnk` to `data\Shortcuts` and `Start Menu\Programs\Taskbar Groups`, then selects it in Explorer; the user pins it (right-click → Show more options → Pin to taskbar). Windows doesn't allow pinning from code.
- **Each group is its own taskbar button** because each shortcut carries its own AppUserModelID (`WindowsTaskbarGroup.Group.<id>`, set via IPropertyStore on the .lnk). The pop-up process sets the same ID (`SetCurrentProcessExplicitAppUserModelID`) before any window, so it belongs to that button. Without distinct IDs, all shortcuts to the same exe would merge into one button.
- **Group icon:** a dark rounded tile with the first four apps in a 2×2 grid (one app fills it; empty = first letter), written as `data\icons\<id>-<hash>.ico` (DIB frames 16–64, PNG at 256). The hash covers the name and first four paths, so a changed group gets a new path and Windows' icon cache can't show the old one. Existing shortcuts are rewritten on every change (`Shortcuts.Refresh`); the taskbar's pinned copy may still need an unpin + pin.
- **Pop-up:** borderless, rounded (DWM), tool window, top-most. It opens beside the taskbar under the cursor (above a bottom taskbar, etc.; inferred from the monitor's working area, so it works on every monitor and with an auto-hidden taskbar) and closes on deactivate / Esc. Click, Enter, arrows or 1–9 launch (shell execute: exe, .lnk, .url, files, folders). About 450 ms from click to shown (measured, `TASKBARGROUP_TIMING=<log file>`).
- **Data:** `data\groups.json` next to the exe (portable; one exe copy = one set of groups; temp file + swap on save). `TASKBARGROUP_HOME` points it elsewhere and turns off Start Menu shortcuts (tests).
- Icons come from the shell (`IShellItemImageFactory`, alpha kept), the same pictures Explorer shows.
- **Add Apps (0.2.0, `AddAppsForm` / `AppSources`):** tick apps from two lists, or Browse Files….
  - **Running Apps:** top-level windows as Alt+Tab shows them (visible, unowned, not tool windows, not cloaked, with a title; shell windows skipped), one per app. Packaged apps (`GetApplicationUserModelId`: Claude, Windows Terminal) and Store apps (ApplicationFrameHost frames: the window's AppUserModelID) become `shell:AppsFolder\<app id>`, because their exe under WindowsApps can't be started directly; the name is the Start menu's (`IShellItem.GetDisplayName`). A cloaked frame (a suspended or minimized Store app, e.g. Settings on 2026-09-28) isn't listed: closed Store apps can leave cloaked frames behind.
  - **On the Taskbar:** the `.lnk` files in `%APPDATA%\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar` (Kurt's: 44), without our own group shortcuts. Store apps pinned to the taskbar aren't kept there. A chosen pin is **copied** into `data\Links` (target, arguments, icon and run-as kept), so it keeps working if it's unpinned later; a copy with the same bytes counts as already in the group.
  - **Unpin after adding (0.3.0, Kurt: consolidating is the point):** a box under the Taskbar list, ticked by default; the button reads Add and Unpin. Pins already in the group can still be ticked, to only unpin them. There's no official unpin API; `AppSources.Unpin` runs the shell's own verb (`InvokeVerb("taskbarunpin")`, then the "Unpin from taskbar" menu entry, which Windows lists for Kurt's pins) **after** the pin was copied to `data\Links`, and counts it done when Windows removes the .lnk from the pinned folder within 3 s. Deleting the .lnk ourselves would leave a dead button, so it's never done. Failures are listed with "right-click → Unpin from taskbar". Not yet confirmed on the real taskbar (Claude can't test it without unpinning Kurt's apps).

## Tests

- `TaskbarGroup.exe --selftest <scratch dir>`: 23 checks (groups.json round trip; shortcut target, arguments and AppUserModelID read back; icon frames; rename replaces the shortcut and icon; refresh never creates shortcuts; pop-up sits above the taskbar; running apps found once each, packaged ones through their app ID; pins listed and copied), plus `sources.txt` (both lists), `tile.png`, `popup.png`, `popup_empty.png`, `settings.png`, `add_running.png`, `add_taskbar.png` to look at. Report in `selftest.txt`, exit code 0 = all passed.
- Not testable by Claude: pinning and clicking the real taskbar button (Kurt).

## Next

- **Hover to expand (phase 2, Kurt 2026-09-28: click first, hover later).** Needs a background process that watches the mouse over the pinned buttons (UI Automation gives each taskbar button's rectangle and name) and opens the pop-up after a short delay. The same resident process can make the pop-up instant (no process start).
- Possibly: Store (UWP) apps via `shell:AppsFolder`, icon/colour choice per group, import/export of groups.

## Conventions (carried over from Kurt's other projects)

- C# / .NET 8 (`net8.0-windows`), WinForms, x64. Sizes in code are for 100% scaling, multiplied by the window's DPI (`S()` in SettingsForm, `s` in PopupForm); WinForms auto-scaling left rows unscaled at 175%.
- Bump `<Version>` in the csproj on every change Kurt will test; the version is in the settings window title.
- After every code change run `build.bat` so `publish\TaskbarGroup.exe` is current.
- Every `.bat` uses CRLF line endings and `goto`-based flow control (no multi-line parenthesized `if/else`).
- UI labels in Title Case (Microsoft style); sentences, hints and status lines in sentence case. US English.
- Every button gets a tooltip.
- Git: commit straight to `main`, one commit per feature, only when Kurt asks. No remote yet.
- Evidence before fixes; work in phases with a verified stopping point.
- The antivirus quarantined freshly built DLLs in the MHO project until `C:\Dev\MHO-UPK-Tools` was excluded. This folder (`C:\Dev\WindowsTaskbarGroup`) isn't excluded yet.
