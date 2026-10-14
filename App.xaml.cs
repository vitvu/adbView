using System.IO;
using System.Windows;

namespace AdbView;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, ev) =>
        {
            try
            {
                var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "adbView", "error.log");
                Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                File.AppendAllText(log, $"[{DateTime.Now}] {ev.Exception}\n\n");
            }
            catch { }
            MessageBox.Show(ev.Exception.Message, "adbView", MessageBoxButton.OK, MessageBoxImage.Error);
            ev.Handled = true;
        };
        base.OnStartup(e);
    }
}
