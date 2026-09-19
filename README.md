# File Labs

A free, dual-pane file manager for Windows 11, with a copy engine that shows what it is doing.

- **Two panes, with tabs** — browser-style tabs in each pane, `Tab` to switch panes,
  `Ctrl+U` to swap them, drag and drop between panes and to/from Explorer.
- **Transfers you can watch** — copy and move run through the Ferry engine: one scan up front
  (totals and every conflict at once), one conflict decision per job, pause and cancel, a live
  speed chart, one queue per destination drive. Same-drive moves are renames.
- **Optional verification** — reads source and copy back and compares xxHash3 digests; on a move,
  a source whose copy doesn't match is kept.
- **Quick View** — `Space` previews images, video, audio, PDF and text without opening anything.
- **Folder sizes** in the list, file versions for `.exe`/`.dll`, and a recency tint on dates.
- **Views** — Details, List and three icon sizes with Explorer's own thumbnails (`Ctrl+Shift+1…5`).
- **The real right-click menu** when you want it — Send to, Open with, 7-Zip and other shell
  extensions — or a compact one that matches the app.
- **Default file manager** (optional) — folders, drives and `Win+E` open in File Labs. Switching
  it off (or uninstalling) hands them back to whatever had them before.
- Acrylic, tint or solid backdrop, with an opacity floor so text stays readable.

No account, no telemetry, nothing it talks to online.

## Keys

| Key | Action |
|---|---|
| `F5` / `F6` | copy / move to the other pane |
| `F7` | new folder |
| `F2` | rename |
| `F8`, `Del` | delete (to the Recycle Bin) |
| `Space` | Quick View |
| `Ctrl+T` / `Ctrl+W` / `Ctrl+Tab` | new / close / next tab |
| `Ctrl+L` | edit the path |
| `Ctrl+F` | filter the list |
| `Ctrl+D` | pin to Quick access |
| `Ctrl+H` | show hidden files |
| `Ctrl+Shift+C` | copy the selected paths |
| `` Ctrl+` `` | terminal here |
| `Alt+Enter` | Properties |

## Building

Needs the .NET 8 SDK; the installer needs [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```powershell
.\build.ps1                 # dist\FileLabs-<version>-setup.exe
.\build.ps1 -SkipInstaller  # self-contained app in dist\app
```

`FileLabs.exe --selftest` runs the copy engine on real files in a temp folder; exit code 0 = pass.

The version lives only in `FileExplorer.csproj`.

## Licence

MIT. Made by [Protagonist Labs](https://protagonistlabs.app/filelabs/).
