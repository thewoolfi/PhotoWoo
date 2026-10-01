using System.IO;
using System.Windows;
using PhotoWoo.Localization;
using PhotoWoo.Updates;

namespace PhotoWoo;

public partial class App : Application
{
    private FileStream? _installationLease;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.FirstOrDefault() == "--photowoo-apply-update")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (e.Args.Length != 2) { Shutdown(2); return; }
            var result = await UpdateInstaller.ApplyPlanAsync(e.Args[1]);
            if (!result.Success || result.Code == "restart-failed")
            {
                UpdateTranslations.Register(); L10n.SetLanguage("auto");
                MessageBox.Show(L10n.Text("updates.failed") + "\n\n" + result.Message, "PhotoWoo", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            Shutdown(result.Success ? 0 : 1);
            return;
        }
        try { _installationLease = UpdateInstaller.AcquireApplicationLease(AppContext.BaseDirectory); }
        catch (IOException)
        {
            UpdateTranslations.Register(); L10n.SetLanguage("auto");
            MessageBox.Show(L10n.Text("updates.installing"), "PhotoWoo", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(); return;
        }
        catch (UnauthorizedAccessException)
        {
            UpdateTranslations.Register(); L10n.SetLanguage("auto");
            MessageBox.Show(L10n.Text("updates.unsupported"), "PhotoWoo", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(); return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "PhotoWoo", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        var window = new MainWindow(e.Args.FirstOrDefault(path => !path.StartsWith("--")));
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _installationLease?.Dispose();
        base.OnExit(e);
    }
}
