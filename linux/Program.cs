using Avalonia;

namespace FileExplorer;

static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--selftest")) return SelfTest.Run();
        return AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
    }
}
