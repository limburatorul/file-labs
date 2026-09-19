using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace FileExplorer;

// key=value lines in %AppData%\FileLabs\settings.txt
public static class Settings
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileLabs");
    static readonly string FilePath = Path.Combine(Dir, "settings.txt");

    public static int Backdrop = 3;          // 1 solid, 2 mica, 3 acrylic
    public static double Opacity = 70;       // 30–100, floor keeps text readable
    public static bool Verify;               // xxHash3 check after copying
    public static bool ShowHidden;
    public static bool NativeMenu;          // right-click shows the Windows menu instead of ours
    public static double SidebarWidth = 270;
    public static double Split = 0.5;                                  // left pane share
    public static Rect? WindowBounds;                                  // restore bounds
    public static bool Maximized;
    public static readonly Dictionary<string, double> ColumnWidths = new(); // "Size" → 80
    public static readonly Dictionary<string, PaneView.ViewMode> Views = new(); // "Left" → Details

    public static void Load()
    {
        if (!File.Exists(FilePath)) return;
        foreach (var kv in File.ReadAllLines(FilePath).Select(l => l.Split('=', 2)).Where(a => a.Length == 2))
        {
            var v = kv[1].Trim();
            switch (kv[0].Trim())
            {
                case "backdrop" when int.TryParse(v, out var b) && b is >= 1 and <= 3: Backdrop = b; break;
                case "opacity" when double.TryParse(v, CultureInfo.InvariantCulture, out var o) && double.IsFinite(o): Opacity = Math.Clamp(o, 30, 100); break;
                case "verify": Verify = v == "1"; break;
                case "hidden": ShowHidden = v == "1"; break;
                case "nativemenu": NativeMenu = v == "1"; break;
                case "split" when double.TryParse(v, CultureInfo.InvariantCulture, out var sp) && double.IsFinite(sp): Split = Math.Clamp(sp, 0.15, 0.85); break;
                case "maximized": Maximized = v == "1"; break;
                case "window": try { var r = Rect.Parse(v); if (!r.IsEmpty) WindowBounds = r; } catch (FormatException) { } break; // hand-edited file
                case var k when k.StartsWith("view.") && Enum.TryParse<PaneView.ViewMode>(v, out var vm): Views[k[5..]] = vm; break;
                case var k when k.StartsWith("col.") && double.TryParse(v, CultureInfo.InvariantCulture, out var cw) && double.IsFinite(cw): ColumnWidths[k[4..]] = Math.Clamp(cw, 30, 800); break;
                case "sidebar" when double.TryParse(v, CultureInfo.InvariantCulture, out var w) && double.IsFinite(w): SidebarWidth = Math.Clamp(w, 160, 600); break;
            }
        }
    }

    public static void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllLines(FilePath, new[]
        {
            $"backdrop={Backdrop}",
            $"opacity={Opacity.ToString(CultureInfo.InvariantCulture)}",
            $"verify={(Verify ? 1 : 0)}",
            $"hidden={(ShowHidden ? 1 : 0)}",
            $"nativemenu={(NativeMenu ? 1 : 0)}",
            $"sidebar={SidebarWidth.ToString(CultureInfo.InvariantCulture)}",
            $"split={Split.ToString(CultureInfo.InvariantCulture)}",
            $"maximized={(Maximized ? 1 : 0)}",
            WindowBounds is { } r ? $"window={r.ToString(CultureInfo.InvariantCulture)}" : "",
        }.Concat(ColumnWidths.Select(c => $"col.{c.Key}={c.Value.ToString(CultureInfo.InvariantCulture)}"))
         .Concat(Views.Select(v => $"view.{v.Key}={v.Value}")));
    }

    // ---- Default file manager (per user, HKCU only; turning it off removes every key it added) ----
    // Folder/drive double-clicks go through the Directory/Drive default verb; Win+E through the
    // "This PC" launcher CLSID. Explorer's own window navigation is unaffected.
    const string Verb = "filelabs";
    const string WinE = @"Software\Classes\CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}";
    static readonly string[] Types = { "Directory", "Drive" };

    public static bool IsDefaultFileManager =>
        (Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\shell")?.GetValue("") as string) == Verb;

    // Whatever was the default before File Labs (e.g. another file manager's verb, or a Win+E
    // override), so switching File Labs off hands folders back to it instead of to nothing.
    const string Backup = @"Software\Protagonist Labs\File Labs\PreviousDefault";

    public static void SetDefaultFileManager(bool on)
    {
        var exe = Environment.ProcessPath;
        var hk = Registry.CurrentUser;
        using var backup = hk.CreateSubKey(Backup);
        foreach (var t in Types)
        {
            var shellPath = $@"Software\Classes\{t}\shell";
            if (on)
            {
                using var shell = hk.CreateSubKey(shellPath);
                if (shell.GetValue("") is string prev && prev != Verb) backup.SetValue(t, prev);
                shell.SetValue("", Verb);
                using var verb = shell.CreateSubKey(Verb);
                verb.SetValue("", "Open in File Labs");
                verb.SetValue("Icon", exe);
                using var cmd = verb.CreateSubKey("command");
                cmd.SetValue("", $"\"{exe}\" \"%1\"");
            }
            else
            {
                using var shell = hk.OpenSubKey(shellPath, writable: true);
                if (shell == null) continue;
                if (shell.GetValue("") as string == Verb)
                {
                    // restore the previous default only if its verb is still there (the other app may be gone)
                    if (backup.GetValue(t) is string prev && shell.OpenSubKey(prev) != null) shell.SetValue("", prev);
                    else shell.DeleteValue("");
                }
                shell.DeleteSubKeyTree(Verb, throwOnMissingSubKey: false);
                backup.DeleteValue(t, throwOnMissingValue: false);
            }
        }

        var winEPath = WinE + @"\shell\opennewwindow\command";
        if (on)
        {
            using var cmd = hk.CreateSubKey(winEPath);
            var prev = cmd.GetValue("") as string;
            if (!string.IsNullOrEmpty(prev) && !prev.Contains(exe, StringComparison.OrdinalIgnoreCase))
            {
                backup.SetValue("WinE", prev);
                backup.SetValue("WinEDelegate", cmd.GetValue("DelegateExecute") as string ?? "");
            }
            cmd.SetValue("", $"\"{exe}\"");
            cmd.SetValue("DelegateExecute", "");
        }
        else if (backup.GetValue("WinE") is string prevWinE)
        {
            using var cmd = hk.CreateSubKey(winEPath);
            cmd.SetValue("", prevWinE);
            cmd.SetValue("DelegateExecute", backup.GetValue("WinEDelegate") as string ?? "");
            backup.DeleteValue("WinE", false); backup.DeleteValue("WinEDelegate", false);
        }
        else hk.DeleteSubKeyTree(WinE + @"\shell\opennewwindow", throwOnMissingSubKey: false); // only what we added

        // Win+E itself: the key above is ignored on recent Windows 11, so a background agent catches it.
        if (on) Agent.Enable(exe); else Agent.Disable();
    }
}
