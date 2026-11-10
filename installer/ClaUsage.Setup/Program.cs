using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace ClaUsage.Setup;

public static class Program
{
    /// <summary>
    /// ClaUsage-Setup.exe             the setup window
    /// ClaUsage-Setup.exe --silent    installs with the defaults, no window (also /S, /silent); exit code 0 on success
    ///   --dir "path"                 install folder for a new install
    ///   --no-autostart               don't start ClaUsage with Windows
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        var silent = args.Any(a => a is "--silent" or "/S" or "/s" or "/silent" or "/SILENT" or "/VERYSILENT");
        if (silent) return RunSilent(args);

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        return app.Run(new SetupWindow());
    }

    private static int RunSilent(string[] args)
    {
        try
        {
            var installer = new Installer();
            var dir = args.SkipWhile(a => a != "--dir").Skip(1).FirstOrDefault();
            if (Installer.Existing is null && !string.IsNullOrWhiteSpace(dir)) installer.Folder = Path.GetFullPath(dir);
            if (args.Contains("--no-autostart")) installer.StartWithWindows = false;
            installer.RunAsync(new Progress<(double, string)>(), CancellationToken.None).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }
}
