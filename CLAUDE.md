# Windows Taskbar Group

A Windows utility that clusters taskbar icons into named group folders. Clicking a group's taskbar button opens a pop-up with its apps. Windows-only. The owner is Kurt.

## Layout

```
TaskbarGroup/   TaskbarGroup.exe: .NET 8 WinForms, x64, single-file framework-dependent (build.bat -> publish\). v0.4.0
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

- Possibly: an All Apps list (every Start menu app, incl. Store apps) in Add Apps, icon/color choice per group, import/export of groups, a faster click path (the `--group` process still starts .NET before handing over).

## Hover to open (0.4.0)

- **Helper:** `TaskbarGroup.exe --watch` (`Watcher` in Hover.cs): hidden window + tray icon (Open Settings, Turn Off Hover, Quit Hover Helper). One per data folder: window title and mutex carry a hash of `Store.Home`, so tests on a scratch folder never touch Kurt's. Settings' checkbox "Open a group when the mouse rests on its taskbar button" (+ delay, default 400 ms, `Store.Hover`) starts it and writes `Startup\Taskbar Groups Hover Helper.lnk`; unticking sends `quit` and removes that. Settings also restarts it at open if hover is on and it isn't running; `--watch` exits at once when hover is off.
- **Which button is under the mouse:** Windows 11 taskbar buttons are UI Automation elements `Taskbar.TaskListButtonAutomationPeer` with AutomationId `Appid: <AppUserModelID>` (Kurt's groups: `Appid: WindowsTaskbarGroup.Group.<id>`; also on the secondary taskbars, `Shell_SecondaryTrayWnd`). The legacy managed UIA client's `FromPoint` stops at `Shell_TrayWnd` (never reaches the buttons), so `TaskbarButtons.At` lists the buttons of the taskbar under the mouse (`FromHandle(tray).FindAll`, cached up to 1 s) and hit-tests their rectangles: ~18 ms with the listing. A background thread polls every 50 ms; `WindowFromPoint` first, so UIA only runs over a taskbar. Needs `UseWPF` (for System.Windows.Automation) plus an explicit `<Using Include="System.IO" />`.
- **Hover pop-up:** `PopupForm` with `hoverZone` = the button: `ShowWithoutActivation`, closes when the mouse has been outside the union of button and pop-up for 400 ms; clicking into it activates it and it then closes on click-away like a clicked one. Not opened while a mouse button is down; reopens only after the mouse leaves the button.
- **Clicks while the helper runs:** the shortcut's `--group` process sends `click <id>` (WM_COPYDATA, after `AllowSetForegroundWindow`) and exits; the helper pins an open hover pop-up of that group, toggles a clicked one closed, or opens it with focus. Without the helper the old path (own pop-up process) is unchanged.
- The self-test adds: every group button found on the real taskbars is hit at its center (read-only), and a scratch helper starts, answers, refuses a second copy and quits.
- **0.4.1 fix (Kurt: "it stays open after you roll off"):** WinForms raises `Activated` when a `TopMost` form is shown, even without focus, and 0.4.0 took that as "clicked into", which stopped the leave check. Now only a real mouse-down on the pop-up (or a click on its taskbar button) pins it; hover pop-ups get WS_EX_TOPMOST via CreateParams instead of the TopMost property, and ignore Deactivate. The self-test reproduces it (a hover pop-up with a fake button far from the mouse must stay in hover mode and close by itself within 2 s): failed on 0.4.0, passes on 0.4.1.
- `--quit-helper` stops this data folder's helper (build.bat runs it first: a running helper locks the exe). A plain `taskkill` (WM_CLOSE) doesn't reach its hidden window.
- Not tested by Claude: the real hover (would move Kurt's mouse).

## Group icons (0.5.0)

- `Group.Icon` (`GroupIcon`): `grid` (default: first four apps), `app` (one item's icon, by its Path) or `custom` (a file in `data\icons\custom`, named `<group id>-<content hash>.<ext>`; copied in, so the original can go). Every kind is drawn in the same frame (`Icons.FramedTile`: dark rounded tile, accent border; Kurt calls it the blue border). `Icons.EffectiveKind` falls back to the grid when the chosen app left the group or the custom file is gone. Custom accepts images (.png/.jpg/.bmp/.gif/.tif) and anything the shell has an icon for (.ico, .exe, .dll), via `Icons.LoadImage`.
- Settings: Change Icon… opens `IconPickerForm` (framed previews of every choice, Choose Image…). The icon file name hashes the choice (kind + app, or custom file + write time), so each change gets a new .ico path.
- `Shortcuts.Create` also rewrites the taskbar's own copy of a pinned group shortcut (User Pinned\TaskBar, found by `--group <id>`) with the new icon, then `SHChangeNotify(SHCNE_UPDATEITEM)` + `SHCNE_ASSOCCHANGED`. Skipped in test mode. **Hypothesis until Kurt confirms** that the taskbar redraws without an unpin + pin.

## Conventions (carried over from Kurt's other projects)

- C# / .NET 8 (`net8.0-windows`), WinForms, x64. Sizes in code are for 100% scaling, multiplied by the window's DPI (`S()` in SettingsForm, `s` in PopupForm); WinForms auto-scaling left rows unscaled at 175%.
- Bump `<Version>` in the csproj on every change Kurt will test; the version is in the settings window title.
- After every code change run `build.bat` so `publish\TaskbarGroup.exe` is current.
- Every `.bat` uses CRLF line endings and `goto`-based flow control (no multi-line parenthesized `if/else`).
- UI labels in Title Case (Microsoft style); sentences, hints and status lines in sentence case. US English.
- Every button gets a tooltip.
- Git: commit straight to `main`, one commit per feature, only when Kurt asks. GitHub: `leeper48/Taskbar-Groups` (created 2026-09-28; `gh` isn't installed, so Kurt creates repos on github.com and Claude pushes).
- Evidence before fixes; work in phases with a verified stopping point.
- The antivirus quarantined freshly built DLLs in the MHO project until `C:\Dev\MHO-UPK-Tools` was excluded. This folder (`C:\Dev\WindowsTaskbarGroup`) isn't excluded yet.
