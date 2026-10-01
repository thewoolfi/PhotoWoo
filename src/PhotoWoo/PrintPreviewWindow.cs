using System.Printing;
using PhotoWoo.Localization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoWoo;

public sealed class PrintPreviewWindow : Window
{
    private const double Mm = 96d / 25.4, MarginSize = 10 * Mm;
    private readonly BitmapSource _bitmap;
    private readonly string _name;
    private readonly ComboBox _paper = new(), _orientation = new(), _placement = new();
    private readonly Viewbox _preview = new() { Stretch = Stretch.Uniform, Margin = new Thickness(26) };
    private readonly TextBlock _printerText = new(), _geometryText = new(), _hint = new();
    private readonly Button _printButton, _chooseButton;
    private PrintDialog? _selectedPrinter;
    private PageGeometry _geometry;
    private bool _updatingChoices;
    private static readonly Brush Fore = new SolidColorBrush(Color.FromRgb(229, 233, 232));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(146, 157, 158));
    private static readonly Brush Back = new SolidColorBrush(Color.FromRgb(29, 32, 34));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(169, 200, 189));

    public PrintPreviewWindow(BitmapSource bitmap, string name)
    {
        _bitmap = bitmap; _name = name;
        Title = L10n.Text("print.title"); Width = 900; Height = 740; MinWidth = 740; MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Back; Foreground = Fore; FontFamily = new FontFamily("Segoe UI Variable, Segoe UI");
        UseLayoutRounding = true;
        SourceInitialized += (_, _) => { int enabled = 1; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref enabled, sizeof(int)); };
        Resources.Add(typeof(ComboBox), ChoiceStyle());
        var root = new Grid { Margin = new Thickness(28) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = L10n.Text("print.heading"), FontSize = 23, FontWeight = FontWeights.Light });
        header.Children.Add(new TextBlock { Text = name, FontSize = 12, Foreground = Muted, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 9, 0, 24) });
        root.Children.Add(header);
        var area = new Grid(); area.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); area.ColumnDefinitions.Add(new() { Width = new GridLength(248) }); Grid.SetRow(area, 1); root.Children.Add(area);
        area.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(23, 25, 27)), Child = _preview, BorderBrush = new SolidColorBrush(Color.FromRgb(48, 52, 55)), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 24, 0) });
        var settings = new StackPanel();
        var scroll = new ScrollViewer { Content = settings, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetColumn(scroll, 1); area.Children.Add(scroll);
        _paper.Items.Add(new PaperChoice(L10n.Text("print.a4"), 210 * Mm, 297 * Mm, PageMediaSizeName.ISOA4));
        _paper.Items.Add(new PaperChoice(L10n.Text("print.photoPaper"), 100 * Mm, 150 * Mm, null)); _paper.SelectedIndex = 0;
        foreach (var text in new[] { L10n.Text("print.portrait"), L10n.Text("print.landscape") }) _orientation.Items.Add(text); _orientation.SelectedIndex = 0;
        foreach (var text in new[] { L10n.Text("print.fit"), L10n.Text("print.fill") }) _placement.Items.Add(text); _placement.SelectedIndex = 0;
        AddChoice(settings, L10n.Text("print.paper"), _paper); AddChoice(settings, L10n.Text("print.orientation"), _orientation); AddChoice(settings, L10n.Text("print.placement"), _placement);
        settings.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(48, 52, 55)), Margin = new Thickness(0, 0, 0, 18) });
        _printerText.FontSize = 13; _printerText.TextWrapping = TextWrapping.Wrap; settings.Children.Add(_printerText);
        _geometryText.FontSize = 12; _geometryText.Foreground = Accent; _geometryText.TextWrapping = TextWrapping.Wrap; _geometryText.Margin = new Thickness(0, 8, 0, 0); _geometryText.LineHeight = 19; settings.Children.Add(_geometryText);
        _hint.FontSize = 12; _hint.Foreground = Muted; _hint.TextWrapping = TextWrapping.Wrap; _hint.LineHeight = 19; _hint.Margin = new Thickness(0, 14, 0, 0); settings.Children.Add(_hint);
        var footer = new Grid { Margin = new Thickness(0, 24, 0, 0) };
        footer.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); Grid.SetRow(footer, 2); root.Children.Add(footer);
        var cancel = MakeButton(L10n.Text("print.cancel")); cancel.IsCancel = true; cancel.HorizontalAlignment = HorizontalAlignment.Left; cancel.Click += (_, _) => Close(); footer.Children.Add(cancel);
        _chooseButton = MakeButton(L10n.Text("print.choose")); _chooseButton.Margin = new Thickness(0, 0, 10, 0); _chooseButton.Click += ChoosePrinter; Grid.SetColumn(_chooseButton, 1); footer.Children.Add(_chooseButton);
        _printButton = MakeButton(L10n.Text("print.send")); _printButton.Foreground = Accent; _printButton.BorderBrush = new SolidColorBrush(Color.FromRgb(71, 91, 83)); _printButton.BorderThickness = new Thickness(1); _printButton.IsEnabled = false; _printButton.Click += Print; Grid.SetColumn(_printButton, 2); footer.Children.Add(_printButton);
        Content = root;
        _paper.SelectionChanged += PageOptionsChanged; _orientation.SelectionChanged += PageOptionsChanged; _placement.SelectionChanged += (_, _) => UpdatePreview();
        InvalidatePrinterSelection();
    }

    private static Button MakeButton(string text)
    {
        var button = new Button { Content = text, Padding = new Thickness(16, 0, 16, 0), Height = 36, MinWidth = 88, Cursor = System.Windows.Input.Cursors.Hand };
        if (Application.Current?.MainWindow?.TryFindResource("QuietButton") is Style style) button.Style = style;
        else { button.Background = Back; button.Foreground = Fore; button.BorderBrush = Muted; }
        return button;
    }
    private static void AddChoice(Panel panel, string label, ComboBox combo)
    {
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Muted, Margin = new Thickness(0, 0, 0, 8) });
        combo.Height = 35; combo.Margin = new Thickness(0, 0, 0, 18); System.Windows.Automation.AutomationProperties.SetName(combo, label); panel.Children.Add(combo);
    }
    private void PageOptionsChanged(object sender, SelectionChangedEventArgs e) { if (!_updatingChoices) InvalidatePrinterSelection(); }
    private void InvalidatePrinterSelection()
    {
        _selectedPrinter = null; _printButton.IsEnabled = false; _printerText.Text = L10n.Text("print.noPrinter");
        _hint.Text = L10n.Text("print.initialHint");
        var paper = (PaperChoice)_paper.SelectedItem;
        var size = _orientation.SelectedIndex == 1 ? new Size(paper.Height, paper.Width) : new Size(paper.Width, paper.Height);
        _geometry = new(size, new Rect(size)); _geometryText.Text = L10n.Format("print.preliminary", DescribeSize(size)); UpdatePreview();
    }
    private void ChoosePrinter(object sender, RoutedEventArgs e)
    {
        try
        {
            var requested = _selectedPrinter?.PrintTicket?.Clone() ?? new PrintTicket();
            var paper = (PaperChoice)_paper.SelectedItem;
            requested.PageMediaSize = paper.MediaName is { } name ? new PageMediaSize(name, paper.Width, paper.Height) : new PageMediaSize(paper.Width, paper.Height);
            requested.PageOrientation = _orientation.SelectedIndex == 1 ? PageOrientation.Landscape : PageOrientation.Portrait;
            var dialog = new PrintDialog { PrintTicket = requested, UserPageRangeEnabled = false, MinPage = 1, MaxPage = 1 };
            if (_selectedPrinter?.PrintQueue is { } previousQueue) dialog.PrintQueue = previousQueue;
            // The native dialog selects settings only; no job is submitted here.
            if (dialog.ShowDialog() != true) return;
            var queue = dialog.PrintQueue ?? throw new InvalidOperationException(L10n.Text("print.chooseAvailable"));
            var basis = queue.UserPrintTicket ?? queue.DefaultPrintTicket ?? new PrintTicket();
            var validation = queue.MergeAndValidatePrintTicket(basis, dialog.PrintTicket ?? requested);
            var ticket = validation.ValidatedPrintTicket;
            var geometry = ReadGeometry(ticket, queue.GetPrintCapabilities(ticket));
            dialog.PrintTicket = ticket; _selectedPrinter = dialog; _geometry = geometry;
            SynchronizeChoices(ticket, geometry);
            _printerText.Text = queue.FullName;
            _geometryText.Text = L10n.Format("print.copies", DescribeSize(geometry.PageSize), ticket.CopyCount ?? 1);
            _hint.Text = validation.ConflictStatus == ConflictStatus.ConflictResolved
                ? L10n.Text("print.adjustedHint")
                : L10n.Text("print.readyHint");
            UpdatePreview(); _printButton.IsEnabled = true;
        }
        catch (Exception ex) { InvalidatePrinterSelection(); MessageBox.Show(this, ex.Message, L10n.Text("print.setupError"), MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private static PageGeometry ReadGeometry(PrintTicket ticket, PrintCapabilities capabilities)
    {
        var media = ticket.PageMediaSize;
        var knownMedia = capabilities.PageMediaSizeCapability.FirstOrDefault(item => media?.PageMediaSizeName is not null && item.PageMediaSizeName == media.PageMediaSizeName);
        double width = media?.Width ?? knownMedia?.Width ?? double.NaN, height = media?.Height ?? knownMedia?.Height ?? double.NaN;
        if (!PositiveFinite(width) || !PositiveFinite(height)) throw new InvalidOperationException(L10n.Text("print.mediaError"));
        if (ticket.PageOrientation is PageOrientation.Landscape or PageOrientation.ReverseLandscape) (width, height) = (height, width);
        var page = new Size(width, height); var area = capabilities.PageImageableArea;
        if (area is null || !PositiveFinite(area.ExtentWidth) || !PositiveFinite(area.ExtentHeight) || !double.IsFinite(area.OriginWidth) || !double.IsFinite(area.OriginHeight))
            throw new InvalidOperationException(L10n.Text("print.areaError"));
        // Physical-page coordinates retain asymmetric hardware margins.
        var imageable = Rect.Intersect(new Rect(page), new Rect(area.OriginWidth, area.OriginHeight, area.ExtentWidth, area.ExtentHeight));
        if (imageable.IsEmpty || imageable.Width <= 0 || imageable.Height <= 0) throw new InvalidOperationException(L10n.Text("print.noArea"));
        var geometry = new PageGeometry(page, imageable); _ = ContentRectangle(geometry); return geometry;
    }
    private void SynchronizeChoices(PrintTicket ticket, PageGeometry geometry)
    {
        _updatingChoices = true;
        try
        {
            var landscape = ticket.PageOrientation is PageOrientation.Landscape or PageOrientation.ReverseLandscape;
            var width = landscape ? geometry.PageSize.Height : geometry.PageSize.Width; var height = landscape ? geometry.PageSize.Width : geometry.PageSize.Height;
            while (_paper.Items.Count > 2) _paper.Items.RemoveAt(_paper.Items.Count - 1);
            var selected = -1;
            for (var i = 0; i < _paper.Items.Count; i++) if (_paper.Items[i] is PaperChoice candidate && Math.Abs(candidate.Width - width) < 1 && Math.Abs(candidate.Height - height) < 1) selected = i;
            if (selected < 0) { selected = _paper.Items.Count; _paper.Items.Add(new PaperChoice(L10n.Format("print.fromPrinter", DescribeSize(new Size(width, height))), width, height, ticket.PageMediaSize?.PageMediaSizeName)); }
            _paper.SelectedIndex = selected; _orientation.SelectedIndex = landscape ? 1 : 0;
        }
        finally { _updatingChoices = false; }
    }
    private static Rect ContentRectangle(PageGeometry geometry)
    {
        var marginBox = new Rect(MarginSize, MarginSize, Math.Max(0, geometry.PageSize.Width - 2 * MarginSize), Math.Max(0, geometry.PageSize.Height - 2 * MarginSize));
        // Intersect page margins with hardware bounds rather than adding the margins twice.
        var content = Rect.Intersect(marginBox, geometry.ImageableArea);
        if (content.IsEmpty || content.Width <= 0 || content.Height <= 0) throw new InvalidOperationException(L10n.Text("print.tooSmall"));
        return content;
    }
    private FixedPage CreatePage()
    {
        var content = ContentRectangle(_geometry);
        var page = new FixedPage { Width = _geometry.PageSize.Width, Height = _geometry.PageSize.Height, Background = Brushes.White, ClipToBounds = true };
        var imageArea = new Border { Width = content.Width, Height = content.Height, ClipToBounds = true,
            Child = new Image { Source = _bitmap, Stretch = _placement.SelectedIndex == 0 ? Stretch.Uniform : Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch } };
        FixedPage.SetLeft(imageArea, content.X); FixedPage.SetTop(imageArea, content.Y); page.Children.Add(imageArea);
        page.Measure(_geometry.PageSize); page.Arrange(new Rect(_geometry.PageSize)); page.UpdateLayout(); return page;
    }
    private void UpdatePreview() => _preview.Child = CreatePage();
    private void Print(object sender, RoutedEventArgs e)
    {
        if (_selectedPrinter is null) return;
        _printButton.IsEnabled = false; _chooseButton.IsEnabled = false;
        try
        {
            var document = new FixedDocument(); document.DocumentPaginator.PageSize = _geometry.PageSize;
            var pageContent = new PageContent(); ((IAddChild)pageContent).AddChild(CreatePage()); document.Pages.Add(pageContent);
            // Same full-page coordinates and crop as the preview; no PrintVisual origin adjustment.
            _selectedPrinter.PrintDocument(document.DocumentPaginator, "PhotoWoo — " + _name); Close();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, L10n.Text("print.sendError"), MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _printButton.IsEnabled = _selectedPrinter is not null; _chooseButton.IsEnabled = true; }
    }
    private static bool PositiveFinite(double value) => double.IsFinite(value) && value > 0;
    private static string DescribeSize(Size size) => L10n.Format("print.size", size.Width / Mm, size.Height / Mm);
    private readonly record struct PageGeometry(Size PageSize, Rect ImageableArea);
    private sealed record PaperChoice(string Label, double Width, double Height, PageMediaSizeName? MediaName) { public override string ToString() => Label; }
    private static Style ChoiceStyle() => (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ComboBox">
          <Setter Property="Foreground" Value="#E5E9E8"/><Setter Property="FontSize" Value="12"/>
          <Setter Property="ItemContainerStyle"><Setter.Value><Style TargetType="ComboBoxItem">
            <Setter Property="Padding" Value="10,8"/><Setter Property="Foreground" Value="#E5E9E8"/><Setter Property="Background" Value="#252A2D"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBoxItem">
              <Border x:Name="Item" Background="{TemplateBinding Background}" Padding="{TemplateBinding Padding}"><ContentPresenter/></Border>
              <ControlTemplate.Triggers><Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Item" Property="Background" Value="#3A4642"/></Trigger>
                <Trigger Property="IsSelected" Value="True"><Setter Property="Foreground" Value="#B5D3C7"/></Trigger></ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter></Style></Setter.Value></Setter>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBox"><Grid>
            <ToggleButton Focusable="False" ClickMode="Press" IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
              <ToggleButton.Template><ControlTemplate TargetType="ToggleButton">
                <Border x:Name="Choice" Background="#252A2D" BorderBrush="#41494B" BorderThickness="1" CornerRadius="4">
                  <TextBlock Text="&#xE70D;" FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets" FontSize="10" Foreground="#A9C8BD" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,11,0"/>
                </Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Choice" Property="Background" Value="#303739"/></Trigger></ControlTemplate.Triggers>
              </ControlTemplate></ToggleButton.Template>
            </ToggleButton>
            <ContentPresenter IsHitTestVisible="False" Margin="11,0,28,0" VerticalAlignment="Center" Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"/>
            <Border x:Name="Focus" IsHitTestVisible="False" BorderBrush="Transparent" BorderThickness="1" CornerRadius="4"/>
            <Popup x:Name="PART_Popup" Placement="Bottom" IsOpen="{TemplateBinding IsDropDownOpen}" AllowsTransparency="True" Focusable="False">
              <Border Background="#252A2D" BorderBrush="#596B62" BorderThickness="1" CornerRadius="4" MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}">
                <ScrollViewer MaxHeight="260" CanContentScroll="True"><ItemsPresenter/></ScrollViewer>
              </Border></Popup>
          </Grid><ControlTemplate.Triggers><Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="Focus" Property="BorderBrush" Value="#A9C8BD"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger></ControlTemplate.Triggers>
          </ControlTemplate></Setter.Value></Setter>
        </Style>
        """);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
