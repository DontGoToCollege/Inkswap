using System.IO;
using System.Windows;

namespace Inkswap;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            PortablePaths.Log("unhandled: " + args.Exception);
            MessageBox.Show(args.Exception.Message, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        // --smoke runs the built-in self-check; --smoke <file.js> runs that script instead (for testing with the model)
        var i = Array.FindIndex(e.Args, a => a.Equals("--smoke", StringComparison.OrdinalIgnoreCase));
        var script = i >= 0 && i + 1 < e.Args.Length ? Path.GetFullPath(e.Args[i + 1]) : null;
        new MainWindow(i >= 0, script).Show();
    }
}
