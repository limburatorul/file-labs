using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FileExplorer;

// F1: everything File Labs does, by topic, searchable — keys, and the features that have no key. The text is
// kept here and nowhere else, so a new shortcut in MainWindow.OnPreviewKeyDown or a new feature needs a line here too.
public partial class HelpWindow : Window
{
    static readonly (string Section, (string Key, string What)[] Rows)[] Help =
    {
        ("The window", new[]
        {
            ("Two panes", "Two folders side by side, each with its own tabs. The active pane has the lighter surface and the accent edge; Tab switches. Ctrl+P shows one pane or two"),
            ("Top row", "The tabs. Drag its empty part (or File Labs' name at the top of the sidebar) to move the window, double-click it to maximize, right-click it for the window menu. Resting on the maximize button shows Windows' snap layouts"),
            ("Sidebar", "Quick access (the folders you pin), Favorites (Home, Desktop, Documents, Downloads, Pictures, Music, Videos, Recycle Bin), Drives with how full each one is, and a Folders tree. Click a section's header to fold it, drag a header to reorder; drag the sidebar's edge to widen it"),
            ("Bottom of the sidebar", "The Drop bin, then Check for updates, What's new, Help and Settings. Hover a button for its name"),
            ("Summary cards", "Under the panes: the drive (free space, how full), this folder (its size, folders and files, and a bar of what it holds by kind: images, video, code…), and the selection — or, with nothing selected, the latest change in the folder"),
            ("Bottom bar", "The everyday commands with their keys, and the status line at its right"),
            ("History button", "Left of minimize: the recently closed tabs and the last 25 folders you visited. Click one to go back to it"),
        }),
        ("Panes and tabs", new[]
        {
            ("Tab", "Switch to the other pane"),
            ("Ctrl+P", "One pane or two (also in Settings and the bottom bar)"),
            ("Ctrl+U", "Swap the two panes"),
            ("Ctrl+T", "New tab"),
            ("Ctrl+W", "Close the tab (pinned tabs stay)"),
            ("Ctrl+Shift+T", "Reopen the tab you closed last; the history button has the rest"),
            ("Ctrl+Tab / Ctrl+Shift+Tab", "Next / previous tab"),
            ("Middle-click a folder", "Open it in a new tab behind this one (a shortcut to a folder too): in the list, the sidebar, the folder tree, the path, and on Back, Forward and Up. On the empty part of the path: the folder above"),
            ("Middle-click a tab", "Close it"),
            ("Right-click a tab", "Pin or unpin it, or close the other tabs. Pinned tabs sit at the left, have no close button, are remembered and stay open"),
            ("Drag a tab", "Reorder, or drop it on the other pane to move it there"),
            ("Many tabs", "Below 120 px each they wrap into more rows, which stay where they are when you switch tabs"),
            ("A folder opened from outside", "From Explorer, another app, a shortcut or Win+E: if a tab already shows it, File Labs switches to that tab instead of opening a second one"),
            ("Double-click the splitter", "Make the two panes equal again"),
            ("Settings → Close the second pane with its last tab", "Closing the right pane's last tab (its ×, Ctrl+W or a middle click) hides the pane; Ctrl+P brings it back with that tab"),
            ("Tabs are remembered", "Every pane's tabs come back the next time File Labs starts"),
        }),
        ("Moving around", new[]
        {
            ("Enter / F3", "Open"),
            ("Backspace / Alt+Up", "Up one folder; the folder you left stays selected"),
            ("Alt+Left / Alt+Right", "Back / Forward (also the mouse side buttons)"),
            ("Ctrl+L", "Type a path; matching folders are suggested, Tab completes, Down picks one. %USERPROFILE% and other variables work"),
            ("Chevron after a path segment", "The folders inside it, to jump sideways"),
            ("Just type (or Ctrl+F)", "Filters this folder from anywhere in the window, no click needed; Enter searches every subfolder, * and ? work, Down goes to the list, Esc clears"),
            ("Esc", "Leave search results"),
            ("Ctrl+D", "Pin the folder (or the selected folders) to Quick access; drag a pinned folder to reorder, right-click it to move or remove it"),
            ("Quick access lists", "Several lists with names you choose: the ··· on Quick access' header, or a right-click on the header, switches between them and adds, renames, reorders or deletes them. Ctrl+D pins to the list shown"),
            ("Pin by dragging", "Drag a folder, a shortcut to one, or a tab onto Quick access: it is pinned where the line shows. Nothing is copied or moved"),
            ("Middle-click empty space", "With Settings → Middle-click autoscroll on: a round marker where you clicked, and the list scrolls toward the pointer, faster the further it is. A left or middle click stops it (or letting go, if you held the button). Settings can keep it to up and down"),
            ("\\\\server", "Type a server's name to see its shared folders; going up from a share comes back to that list"),
        }),
        ("Files", new[]
        {
            ("Ctrl+C / Ctrl+X / Ctrl+V", "Copy, cut, paste, also with Explorer. Right-click a folder, or a shortcut to one, → Paste into folder puts the files in it"),
            ("F2", "Rename in place; with several selected, rename them all: a pattern with [N] name, [C] counter and [E] extension, find and replace, and a preview of every new name"),
            ("Click a selected name", "Rename it, as in Explorer: a slow second click, not a double-click"),
            ("Drag files", "Same drive: move. Other drive: copy. Ctrl copies, Shift moves — to the other pane, Explorer or another app. The folder they would go into lights up; a shortcut to a folder takes them into that folder. The wheel scrolls the list while you drag"),
            ("Drop on the drive strip", "The thin bar between the path and the files: whatever is dropped there goes into the folder shown, whatever is selected"),
            ("Ctrl+drag in the same folder", "Duplicate, as in Explorer: “name - Copy.ext”, then “name - Copy (2).ext”; folders too. Pasting copied files into their own folder does the same"),
            ("F7 / Ctrl+Shift+N", "New folder"),
            ("Ctrl+N", "New file: the types Explorer's New menu offers, plus text, Markdown, JSON, CSV, PowerShell and batch"),
            ("F8 / Del", "Delete to the Recycle Bin; on a drive with its Recycle Bin turned off, it asks first, since that delete is for good"),
            ("Shift+Del, Enter", "Delete permanently; the next file is selected, to carry on from there"),
            ("Ctrl+Z", "Undo the last copy, move, rename, new file or folder, zip, extract or shortcut"),
            ("Ctrl+Shift+C", "Copy the selected paths as text; Copy name is in the right-click menu"),
            ("Alt+Enter", "Properties; with several items selected, Windows' combined dialog with their total size"),
            ("Enter on a script", "Runs .bat and .cmd files in their own folder, as Explorer does; F4 edits them. .ps1 opens in the editor"),
            ("Recycle Bin", "In Favorites: what was deleted, from where and when; Restore puts items back, and it can delete them for good or empty the bin"),
        }),
        ("Copy, move and the Drop bin", new[]
        {
            ("Transfers", "Every copy and move is a card in the Transfers window, which opens with it and comes forward when files already exist (the Transfers button at the bottom brings it back): one scan first (so the time left is real), progress, speed over time as a chart, pause, cancel. Several to the same drive run one after another; to different drives, together"),
            ("Files already there", "One question for the whole transfer, inside its card: Keep both, Replace if newer, Replace, or Skip"),
            ("Moving on one drive", "Instant at any size: nothing is copied, the files are renamed into place. A folder moved onto a folder of the same name is merged, file by file"),
            ("Settings → Transfers → Verify", "Reads both copies back after copying and compares them (xxHash3)"),
            ("Ctrl+F5 / F6", "Copy / move the selection to the other pane (with one pane, the first press brings it back)"),
            ("Right-click → Copy to / Move to", "The folders your copies and moves went to lately, or Choose a folder…"),
            ("Right-click → Group into new folder…", "Makes a folder here, named as you like, and moves the selected items into it. Ctrl+Z puts them back"),
            ("Drop bin", "Collect files from several folders, then send them all at once: drag them onto the bin button at the bottom of the sidebar (or onto the open bin), or press Ctrl+B on a selection. Go to the destination and press Copy here or Move here. The bin only remembers the files; nothing moves until you press one of those"),
            ("Ctrl+B", "Add the selection to the Drop bin"),
        }),
        ("Selecting", new[]
        {
            ("Drag on empty space", "Select with a rectangle; Ctrl adds to the selection. In the Recycle Bin, from anywhere, with either button"),
            ("Click on empty space", "Clears the selection; Up and Down carry on from the file you were on"),
            ("Right-drag", "Select with a rectangle from anywhere in the list; letting go opens the menu for what it selected. A plain right-click works as before"),
            ("Num +", "Select by pattern, like *.jpg;*.png"),
            ("Num −", "Deselect by pattern"),
            ("Num *", "Invert the selection"),
        }),
        ("Search and preview", new[]
        {
            ("Filter box", "At the right of the path: typing filters this folder; Enter searches every subfolder and shows where each result was found. Esc leaves the results"),
            ("Search terms", "In the filter box, beside the words of the name: size:>10MB, size:<1KB, size:10MB-1GB · within:today, within:7d (d, w, m, y) · after:2026-10-01, before:2026-01-01 · re:\"^IMG_\\d+\" for a regular expression · content:\"some text\" to look inside files (on Enter, up to 64 MB). The funnel button beside the box writes them for you"),
            ("Space", "Quick View: pictures, video and audio (←/→ skip 5 s), PDF, text and code, zip contents, fonts, folders, and a hex view for anything else; Windows' own previewers where installed. Up/Down move through the list while it is open; Space or Esc closes it"),
            ("Alt+P", "Preview pane: the selected file beside the panes, following the active pane; drag its edge to size it"),
            ("Settings → Files → QuickLook", "Space opens QuickLook instead, when it is installed and running"),
            ("Backups", "Stash and Backup Labs backups show what is inside them (apps, mail folders, files) in the preview, and open in their app"),
            ("Videos Reel knows", "With Reel installed, its poster, title and whether you watched it, above the video's preview"),
        }),
        ("Tools", new[]
        {
            ("F4", "Edit in the built-in code editor: tabs, syntax colouring, a status bar for encoding, line endings, indentation and language, Markdown preview (Ctrl+Shift+V). It reopens what you had open"),
            ("Right-click → Compare in editor", "Two selected files side by side in the editor"),
            ("Shift+F2", "Compare the two panes: on each side, what the other lacks or has older (or of another size) is selected"),
            ("Right-click → Find duplicates…", "Files that are exactly alike in this folder, and in its subfolders if you tick that, plus shortcuts that open the same thing. Select extra copies keeps the oldest of each; send the rest to the Recycle Bin or move them. It works in the background, with progress and Stop"),
            ("Compress to ZIP / Extract", "From the right-click menu. Extract here first checks that nothing would be overwritten"),
            ("Inside an archive", "Enter on a ZIP, TAR or TAR.GZ opens it like a folder. Enter on a file inside opens a copy of it; Ctrl+F5 or Extract to… takes the selection out (nothing already there is overwritten); with a ZIP, Del takes items out of it and dropping or pasting files adds them"),
            ("Disk images", "Right-click an ISO, IMG, VHD or VHDX → Mount: Windows mounts it and it opens as a new drive. Eject is in the drive's right-click menu"),
            ("Checksum (SHA-256)", "Of one file, in a window you can copy from; a hash already on the clipboard is compared with it"),
            ("Create shortcut", "“name - Shortcut” beside the original"),
            ("Run as administrator", "For programs, scripts and installers"),
            ("Ctrl+`", "A terminal in this folder (Windows Terminal, or PowerShell)"),
            ("Windows menu…", "Windows' own right-click menu, with everything other apps add to it. Settings → Files makes it the default right-click"),
            ("Show in File Explorer", "Opens Explorer on the item, for the rare thing only Explorer does"),
            ("Edit with Notepad++", "When Notepad++ is installed"),
        }),
        ("View", new[]
        {
            ("Ctrl+Shift+1…5", "Details, List, icons M, L, XL"),
            ("Ctrl+wheel", "Zoom through the views"),
            ("Right-click empty space → View", "The same five views, from the menu"),
            ("Click a column header", "Sort; the selected file stays in sight. Each folder remembers its own order"),
            ("Drag a column's edge", "Widen it; once you size Name yourself it stays, and a wide list scrolls sideways. Double-click Name's edge to let it fill the pane again"),
            ("Colours by age", "The bar at the left of each row and the age badge go from red (minutes ago) through orange and yellow to blue-grey (a year): what changed lately stands out"),
            ("Sync badges", "Google Drive, Nextcloud and other apps' status icons, on the file icons as in Explorer, updated live"),
            ("Ctrl+H", "Show hidden files"),
            ("F5 / Ctrl+R", "Refresh and measure folder sizes again"),
            ("Folder sizes", "Measured in the background and remembered, so a big drive shows its folders' sizes at once the next time"),
        }),
        ("Windows integration", new[]
        {
            ("Settings → Use File Labs as the default file manager", "Folders and drives you open anywhere in Windows open in File Labs; switching it off gives them back to what had them before"),
            ("Win+E", "Opens File Labs, or brings it forward, while it is the default file manager. Settings can leave Win+E to File Explorer"),
            ("Show in folder", "When another app asks Explorer to show a file (Open path in Everything, Show in folder in a browser), File Labs opens its folder with the file selected"),
            ("Right-click a drive or a folder in the sidebar", "Windows' own menu for it: Properties, Eject, Format, BitLocker and what other apps add"),
            ("Ctrl+G in an Open or Save dialog", "Takes the dialog to the folder File Labs is showing (while File Labs is the default file manager)"),
            ("Settings → Close to the notification area", "Closing hides File Labs next to the clock instead of quitting, so it opens again at once. Click the icon to bring it back; right-click it → Quit to really close"),
        }),
        ("Works with other apps", new[]
        {
            ("Scan with SpaceScan", "What takes the room in a folder or drive"),
            ("Add to Shelf", "Folders and games (.exe) into Shelf's library"),
            ("Add to Reel", "Folders and videos into Reel"),
            ("Back up with Backup Labs", "The selection, or the folder you are in"),
            ("Lume", "Your Quick access folders show up in Lume's search"),
            ("Not installed?", "Each of these opens the app's page instead. Settings → Works with lists them"),
        }),
        ("Settings and updates", new[]
        {
            ("Ctrl+,", "Settings: appearance (backdrop, opacity, background picture, accent colour, row height, text size per area, summary cards, animations), files, the right-click menu, transfers, Windows integration, about"),
            ("Updates", "File Labs checks every hour and asks before updating; the update downloads, is checked against GitHub's published SHA-256, installs and restarts in about ten seconds"),
            ("What's new", "Every release's notes, by month, from the button at the bottom of the sidebar. It opens by itself after an update"),
            ("Settings → Reset to defaults", "Every setting back to how File Labs comes, after asking; each card has its own Reset. Windows integration, tabs and Quick access stay. A double-click on a slider's handle resets just that slider"),
            ("Tooltips", "Wait 1.5 s before they show, each one; Settings → Files → Tooltip delay changes that, from the next start"),
            ("F1", "This help"),
        }),
    };

    public HelpWindow(Window owner)
    {
        InitializeComponent();
        Owner = owner;
        SourceInitialized += (_, _) => MainWindow.ApplyAcrylic(this);
        Root.Background = MainWindow.Tint();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        // Laying out every topic took 150-250 ms before the first frame; the window comes up empty and they follow on
        // the next one, while it fades in (Motion).
        ContentRendered += (_, _) => { Body.Visibility = Visibility.Visible; Find.Focus(); };
        Show("");
    }

    void Find_Changed(object s, TextChangedEventArgs e)
    {
        FindHint.Visibility = Find.Text == "" ? Visibility.Visible : Visibility.Collapsed;
        Show(Find.Text.Trim());
    }

    void Show(string query)
    {
        Sections.Children.Clear();
        Topics.Children.Clear();
        foreach (var (section, rows) in Help)
        {
            var hits = rows.Where(r => query == "" || r.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
                                                    || r.What.Contains(query, StringComparison.OrdinalIgnoreCase)
                                                    || section.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0) continue;
            var heading = new TextBlock { Text = section, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new(0, Sections.Children.Count == 0 ? 0 : 18, 0, 6) };
            Sections.Children.Add(heading);
            var topic = new Button { Content = $"{section}", Style = (Style)FindResource("Side"), Margin = new(0, 1, 0, 1), HorizontalContentAlignment = HorizontalAlignment.Left };
            topic.Click += (_, _) => Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + heading.TranslatePoint(new Point(0, 0), Scroller).Y);
            Topics.Children.Add(topic);
            foreach (var (key, what) in hits)
            {
                var row = new Grid { Margin = new(0, 3, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                var k = new TextBlock { Text = key, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11.5, Foreground = (Brush)FindResource("Accent"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 12, 0) };
                var w = new TextBlock { Text = what, TextWrapping = TextWrapping.Wrap, Foreground = MainWindow.Hex("#C9D3E0") };
                Grid.SetColumn(w, 1);
                row.Children.Add(k); row.Children.Add(w);
                Sections.Children.Add(row);
            }
        }
        if (Sections.Children.Count == 0)
            Sections.Children.Add(new TextBlock { Text = "Nothing matches.", Foreground = MainWindow.Hex("#8A97AA") });
        Scroller.ScrollToTop();
    }
}
