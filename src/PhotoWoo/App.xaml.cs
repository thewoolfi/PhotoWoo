using System.Windows;

namespace PhotoWoo;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "PhotoWoo", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        var window = new MainWindow(e.Args.FirstOrDefault(path => !path.StartsWith("--")));
        MainWindow = window;
        window.Show();
    }
}
