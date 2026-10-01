using System.IO;
using System.Windows;
using System.Windows.Controls;
using PhotoWoo.Imaging;
using PhotoWoo.Localization;
using PhotoWoo.Models;

namespace PhotoWoo;

public partial class MainWindow
{
    private readonly ModelService _models = new();
    private LoadedModel? _modelLoaded;

    private void ClearModel()
    {
        _modelLoaded = null;
        ModelView.SetModel(null);
        ModelView.Visibility = Visibility.Collapsed;
        Viewport.Visibility = Visibility.Visible;
    }

    private void UpdateModelControls()
    {
        var modelMode = _path is not null && SupportedFiles.IsModel(_path);
        ImageEditTools.Visibility = modelMode ? Visibility.Collapsed : Visibility.Visible;
        FullQualityButton.Visibility = modelMode ? Visibility.Collapsed : Visibility.Visible;
        InfoHintText.SetResourceReference(TextBlock.TextProperty, modelMode ? "L10n.models.hint" : "L10n.main.rawHint");
        if (_modelLoaded is not { } model) return;
        StatusText.Text = L10n.Text("models.controls");
        ZoomText.Text = $"{ModelView.ZoomPercent:N0}%";
        var format = Path.GetExtension(_path!).TrimStart('.').ToUpperInvariant();
        DetailsText.Text = L10n.Format("models.details", format, model.Triangles);
        InfoNameText.Text = Path.GetFileName(_path);
        InfoDetailsText.Text = L10n.Format("models.info", format, model.Meshes, model.Triangles, model.Animations);
        try { InfoDetailsText.Text += "\n" + L10n.Format("main.fileSize", new FileInfo(_path!).Length / 1048576d); }
        catch (IOException) { }
    }
}
