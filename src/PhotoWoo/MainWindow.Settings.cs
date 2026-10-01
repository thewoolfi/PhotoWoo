using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using PhotoWoo.Interactions;
using PhotoWoo.Localization;

namespace PhotoWoo;

public partial class MainWindow
{
    private void ShowSettings(string initialTab = "viewing")
    {
        EndImageDrag();
        var dialog = new SettingsWindow(_settings, initialTab) { Owner = this };
        dialog.CheckUpdatesRequested += (_, _) => ShowUpdates(dialog);
        if (dialog.ShowDialog() == true)
        {
            _settings = dialog.ResultSettings;
            MotionPreferences.AnimationsEnabled = _settings.Animations;
            MotionPreferences.InertiaEnabled = _settings.Inertia;
            L10n.SetLanguage(_settings.Language);
            AnimatePanel(FilmstripBorder, _settings.Filmstrip, HeightProperty, 111);
            FilmstripButton.Foreground = new SolidColorBrush(_settings.Filmstrip ? Color.FromRgb(169, 200, 189) : Color.FromRgb(213, 218, 219));
            RefreshFullscreenSettings();
            RefreshUpdateSchedule();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
                File.WriteAllText(SettingsFile, JsonSerializer.Serialize(_settings));
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }
        UpdateControls();
        StatusText.Text = L10n.Text("main.ready");
    }

    private void Support_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(ViewerSettings.DefaultSupportUrl) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(L10n.Format("main.linkFailed", ex.Message)); }
    }
}
