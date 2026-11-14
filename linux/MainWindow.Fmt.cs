namespace FileExplorer;

// Backups.cs (shared with Windows) formats sizes through MainWindow.Fmt, as on Windows.
public partial class MainWindow
{
    public static string Fmt(long b) => FileExplorer.Fmt.Size(b);
}
