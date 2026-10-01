using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoWoo.Imaging;
using PhotoWoo.Integration;
using PhotoWoo.Localization;

namespace PhotoWoo;

public sealed class SettingsWindow : Window
{
    private readonly ViewerSettings _draft;
    private readonly string _originalLanguage;
    private string _selectedTab;
    private TextBlock _status = new();
    private bool _accepted;
    private static readonly Brush Back = new SolidColorBrush(Color.FromRgb(29, 32, 34));
    private static readonly Brush Fore = new SolidColorBrush(Color.FromRgb(229, 233, 232));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(151, 162, 161));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(169, 200, 189));

    public ViewerSettings ResultSettings { get; private set; }
    public event EventHandler? CheckUpdatesRequested;
    public SettingsWindow() : this(new ViewerSettings()) { }

    public SettingsWindow(ViewerSettings settings, string initialTab = "viewing")
    {
        _draft = settings.Copy(); ResultSettings = settings.Copy();
        _originalLanguage = settings.Language;
        _selectedTab = initialTab;
        Width = 960; Height = 790; MinWidth = 840; MinHeight = 650;
        ResizeMode = ResizeMode.CanResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Back; Foreground = Fore;
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI"); FontSize = 13;
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        SourceInitialized += (_, _) => { int dark = 1; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int)); };
        Closed += (_, _) => { if (!_accepted) L10n.SetLanguage(_originalLanguage); };
        Resources.Add(typeof(ComboBox), ChoiceStyle());
        Resources.Add(typeof(CheckBox), ToggleStyle());
        Resources.Add(typeof(TabControl), TabControlStyle());
        Resources.Add(typeof(TabItem), TabItemStyle());
        BuildContent();
    }

    private void BuildContent()
    {
        Title = L10n.Text("settings.title");
        var root = new Grid { Margin = new Thickness(30, 24, 30, 22) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 22) };
        header.Children.Add(new TextBlock { Text = L10n.Text("settings.title"), FontSize = 24, FontWeight = FontWeights.Light });
        header.Children.Add(Paragraph(L10n.Text("settings.subtitle"), 8));
        root.Children.Add(header);

        var tabs = new TabControl { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        AddTab(tabs, "viewing", ViewingPage());
        AddTab(tabs, "controls", ControlsPage());
        AddTab(tabs, "windows", WindowsPage());
        AddTab(tabs, "updates", UpdatesPage());
        AddTab(tabs, "about", AboutPage());
        tabs.SelectedItem = tabs.Items.Cast<TabItem>().FirstOrDefault(item => Equals(item.Tag, _selectedTab)) ?? tabs.Items[0];
        tabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.Source, tabs) && tabs.SelectedItem is TabItem selected) _selectedTab = (string)selected.Tag; };
        Grid.SetRow(tabs, 1); root.Children.Add(tabs);

        var footer = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        footer.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _status = Paragraph("", 0); _status.VerticalAlignment = VerticalAlignment.Center; _status.Margin = new Thickness(0, 0, 18, 0);
        footer.Children.Add(_status);
        var cancel = MakeButton(L10n.Text("settings.cancel")); cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        Grid.SetColumn(cancel, 1); footer.Children.Add(cancel);
        var save = MakeButton(L10n.Text("settings.save"), true); save.IsDefault = true; save.Margin = new Thickness(10, 0, 0, 0);
        save.Click += (_, _) =>
        {
            _draft.SupportUrl = ViewerSettings.DefaultSupportUrl;
            _draft.FullscreenHideDelaySeconds = Math.Clamp(_draft.FullscreenHideDelaySeconds, 1, 10);
            ResultSettings = _draft.Copy(); _accepted = true; DialogResult = true;
        };
        Grid.SetColumn(save, 2); footer.Children.Add(save);
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        Content = root;
    }

    private static void AddTab(TabControl tabs, string name, FrameworkElement content)
    {
        tabs.Items.Add(new TabItem
        {
            Header = L10n.Text("settings.tab." + name), Tag = name,
            Content = new ScrollViewer
            {
                Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 22, 8, 0), CanContentScroll = false
            }
        });
    }

    private FrameworkElement ViewingPage()
    {
        var page = new StackPanel();
        var languageRow = new Grid();
        languageRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        languageRow.ColumnDefinitions.Add(new() { Width = new GridLength(236) });
        var languageLabel = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
        languageLabel.Children.Add(Label("settings.language"));
        languageLabel.Children.Add(Paragraph(L10n.Text("settings.language.hint"), 6));
        languageRow.Children.Add(languageLabel);
        var language = new ComboBox { ItemsSource = L10n.AvailableLanguages, SelectedValuePath = "Code", SelectedValue = _draft.Language, Height = 36, VerticalAlignment = VerticalAlignment.Top };
        if (language.SelectedItem is null) language.SelectedValue = "auto";
        System.Windows.Automation.AutomationProperties.SetName(language, L10n.Text("settings.language"));
        language.SelectionChanged += (_, _) =>
        {
            if (language.SelectedValue is not string code || code == _draft.Language) return;
            _draft.Language = code; L10n.SetLanguage(code);
            // Rebuild only this small dialog; the main viewer updates through dynamic resources.
            Dispatcher.BeginInvoke(new Action(BuildContent));
        };
        Grid.SetColumn(language, 1); languageRow.Children.Add(language);
        page.Children.Add(languageRow);
        page.Children.Add(Section("settings.motion"));
        page.Children.Add(Toggle("settings.animations", "settings.animations.hint", _draft.Animations, value => _draft.Animations = value));
        page.Children.Add(Toggle("settings.inertia", "settings.inertia.hint", _draft.Inertia, value => _draft.Inertia = value));
        page.Children.Add(Toggle("settings.filmstrip", "settings.filmstrip.hint", _draft.Filmstrip, value => _draft.Filmstrip = value));
        page.Children.Add(Section("settings.fullscreen"));
        page.Children.Add(Toggle("settings.fill", "settings.fill.hint", _draft.FullscreenFill, value => _draft.FullscreenFill = value));
        var delayRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var delay = new ComboBox { Width = 164, Height = 36, SelectedValuePath = "Value", DisplayMemberPath = "Label" };
        var delays = new[] { 1d, 2d, 3d, 5d, 10d };
        foreach (var seconds in delays) delay.Items.Add(new DelayChoice(seconds, L10n.Format("settings.delayFormat", seconds)));
        delay.SelectedValue = delays.Contains(_draft.FullscreenHideDelaySeconds) ? _draft.FullscreenHideDelaySeconds : 2d;
        delay.SelectionChanged += (_, _) => { if (delay.SelectedValue is double seconds) _draft.FullscreenHideDelaySeconds = seconds; };
        DockPanel.SetDock(delay, Dock.Right); delayRow.Children.Add(delay);
        var label = Label("settings.hideDelay"); label.VerticalAlignment = VerticalAlignment.Center; delayRow.Children.Add(label);
        System.Windows.Automation.AutomationProperties.SetName(delay, L10n.Text("settings.hideDelay"));
        page.Children.Add(delayRow);
        return page;
    }

    private FrameworkElement ControlsPage()
    {
        var page = new Grid();
        page.ColumnDefinitions.Add(new() { Width = new GridLength(1.08, GridUnitType.Star) });
        page.ColumnDefinitions.Add(new() { Width = new GridLength(30) });
        page.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var keyboard = new StackPanel();
        keyboard.Children.Add(Section("help.keyboard", 0));
        foreach (var entry in new (string Shortcut, string Key)[]
        {
            ("Ctrl+O", "open"), ("← / →", "browse"), ("− / +", "zoom"), ("0", "fit"),
            ("R / Shift+R", "rotate"), ("Ctrl+S", "save"), ("Ctrl+Shift+S", "saveCopy"),
            ("Ctrl+Z", "undo"), ("Ctrl+P", "print"), ("T", "filmstrip"), ("I", "info"),
            ("F / F11", "fullscreen"), ("F1", "shortcuts"), ("Esc", "escape")
        }) keyboard.Children.Add(HelpRow(entry.Shortcut, L10n.Text("help." + entry.Key)));
        page.Children.Add(keyboard);
        var mouse = new StackPanel();
        mouse.Children.Add(Section("help.mouse", 0));
        foreach (var entry in new (string Glyph, string Key)[] { ("↕", "wheel"), ("×2", "doubleClick"), ("↔", "drag"), ("▤", "stripDrag"), ("↓", "drop") })
            mouse.Children.Add(HelpRow(entry.Glyph, L10n.Text("help." + entry.Key), compactKey: true));
        Grid.SetColumn(mouse, 2); page.Children.Add(mouse);
        return page;
    }

    private FrameworkElement WindowsPage()
    {
        var page = new StackPanel();
        page.Children.Add(new TextBlock { Text = L10n.Text("settings.windows.title"), FontSize = 21, FontWeight = FontWeights.Light });
        page.Children.Add(Paragraph(L10n.Text("settings.windows.intro"), 9));
        var identity = new DockPanel { Margin = new Thickness(0, 24, 0, 24) };
        var previewPath = Path.Combine(AppContext.BaseDirectory, "Assets", "PhotoWoo.ImageFile.png");
        if (File.Exists(previewPath))
            identity.Children.Add(new Image { Source = new BitmapImage(new Uri(previewPath)), Width = 68, Height = 80, Margin = new Thickness(0, 0, 20, 0) });
        var description = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        description.Children.Add(Label("settings.icon.title"));
        description.Children.Add(Paragraph(L10n.Text("settings.icon.hint"), 7));
        identity.Children.Add(description); page.Children.Add(identity);
        page.Children.Add(Paragraph(L10n.Text("settings.windows.permanent"), 0));
        var defaults = MakeButton(L10n.Text("settings.windows.chooseDefault"), true);
        defaults.Margin = new Thickness(0, 20, 0, 0); defaults.HorizontalAlignment = HorizontalAlignment.Left;
        var note = Paragraph(L10n.Text("settings.windows.note"), 12);
        defaults.Click += (_, _) =>
        {
            try
            {
                var executable = Environment.ProcessPath ?? throw new InvalidOperationException(L10n.Text("settings.windows.exeError"));
                FileAssociations.Register(executable, Path.Combine(AppContext.BaseDirectory, "Assets", "PhotoWoo.ImageFile.ico"), ImageService.SupportedExtensions);
                FileAssociations.OpenDefaultAppsSettings();
                note.Text = L10n.Text("settings.windows.done");
            }
            catch (Exception ex) { note.Text = L10n.Format("settings.windows.error", ex.Message); }
        };
        page.Children.Add(defaults); page.Children.Add(note);
        return page;
    }

    private FrameworkElement UpdatesPage()
    {
        var page = new StackPanel();
        page.Children.Add(new TextBlock { Text = L10n.Text("settings.tab.updates"), FontSize = 21, FontWeight = FontWeights.Light });
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        page.Children.Add(Paragraph(L10n.Format("updates.installedVersion", version is null ? "0.3" : $"{version.Major}.{version.Minor}.{version.Build}"), 9));
        var options = Toggle("updates.autoCheck", "updates.autoCheckHint", _draft.CheckUpdatesAutomatically, value => _draft.CheckUpdatesAutomatically = value);
        options.Margin = new Thickness(0, 27, 0, 20); page.Children.Add(options);
        page.Children.Add(Paragraph(L10n.Text("updates.settingsDescription"), 0));
        var check = MakeButton(L10n.Text("updates.check"), true);
        check.HorizontalAlignment = HorizontalAlignment.Left; check.Margin = new Thickness(0, 22, 0, 0);
        check.Click += (_, _) => CheckUpdatesRequested?.Invoke(this, EventArgs.Empty);
        page.Children.Add(check);
        return page;
    }

    private FrameworkElement AboutPage()
    {
        var page = new StackPanel();
        page.Children.Add(new TextBlock { Text = "PhotoWoo", FontSize = 30, FontWeight = FontWeights.Light });
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        page.Children.Add(Paragraph(L10n.Format("settings.version", version is null ? "0.3" : $"{version.Major}.{version.Minor}.{version.Build}"), 6));
        page.Children.Add(new TextBlock { Text = L10n.Text("settings.about.title"), FontSize = 18, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 26, 0, 0) });
        page.Children.Add(Paragraph(L10n.Text("settings.about.description"), 10));
        page.Children.Add(Section("settings.support.title", 30));
        page.Children.Add(Paragraph(L10n.Text("settings.support.hint"), 0));
        var open = MakeButton(L10n.Text("settings.support.open"), true);
        open.HorizontalAlignment = HorizontalAlignment.Left; open.Margin = new Thickness(0, 18, 0, 0);
        open.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(ViewerSettings.DefaultSupportUrl) { UseShellExecute = true }); }
            catch (Exception ex) { _status.Text = L10n.Format("settings.support.openError", ex.Message); }
        };
        page.Children.Add(open);
        return page;
    }

    private static FrameworkElement Toggle(string title, string hint, bool initial, Action<bool> changed)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 15) };
        var toggle = new CheckBox { IsChecked = initial, Width = 44, Height = 32, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 5, 0) };
        System.Windows.Automation.AutomationProperties.SetName(toggle, L10n.Text(title));
        toggle.Checked += (_, _) => changed(true); toggle.Unchecked += (_, _) => changed(false);
        DockPanel.SetDock(toggle, Dock.Right); row.Children.Add(toggle);
        var text = new StackPanel(); text.Children.Add(Label(title)); text.Children.Add(Paragraph(L10n.Text(hint), 4)); row.Children.Add(text);
        return row;
    }

    private static FrameworkElement HelpRow(string shortcut, string description, bool compactKey = false)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, compactKey ? 18 : 11) };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(compactKey ? 36 : 106) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock { Text = shortcut, Foreground = Accent, FontSize = 11.5, FontFamily = new FontFamily("Consolas, Segoe UI"), Margin = new Thickness(0, 2, 8, 0) });
        var text = Paragraph(description, 0); Grid.SetColumn(text, 1); row.Children.Add(text);
        return row;
    }

    private static TextBlock Label(string key) => new() { Text = L10n.Text(key), FontSize = 13, Foreground = Fore, TextWrapping = TextWrapping.Wrap };
    private static TextBlock Section(string key, double top = 22) => new() { Text = L10n.Text(key), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Fore, Margin = new Thickness(0, top, 0, 15) };
    private static TextBlock Paragraph(string text, double top) => new() { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, LineHeight = 18, Foreground = Muted, Margin = new Thickness(0, top, 0, 0) };
    private static Button MakeButton(string text, bool primary = false)
    {
        var button = new Button { Content = text, Height = 36, Padding = new Thickness(15, 0, 15, 0), Cursor = System.Windows.Input.Cursors.Hand };
        if (Application.Current?.MainWindow?.TryFindResource("QuietButton") is Style style) button.Style = style;
        else { button.Background = Back; button.Foreground = Fore; button.BorderBrush = new SolidColorBrush(Color.FromRgb(65, 73, 75)); }
        if (primary) { button.Foreground = Accent; button.BorderBrush = new SolidColorBrush(Color.FromRgb(66, 87, 77)); }
        return button;
    }
    private sealed record DelayChoice(double Value, string Label) { public override string ToString() => Label; }

    private static Style ToggleStyle() => (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="CheckBox">
          <Setter Property="Cursor" Value="Hand"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="CheckBox">
            <Grid Background="Transparent"><Grid Width="34" Height="20">
              <Border x:Name="Track" Background="#323B3A" BorderBrush="#596561" BorderThickness="1" CornerRadius="10"/>
              <Ellipse x:Name="Thumb" Fill="#ABBAB3" Width="12" Height="12" HorizontalAlignment="Left" Margin="4,0,0,0"/>
            </Grid></Grid><ControlTemplate.Triggers>
              <Trigger Property="IsChecked" Value="True"><Setter TargetName="Track" Property="Background" Value="#769C8C"/><Setter TargetName="Thumb" Property="Fill" Value="#E4EFEA"/><Setter TargetName="Thumb" Property="Margin" Value="18,0,0,0"/></Trigger>
              <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Track" Property="BorderBrush" Value="#E4EFEA"/></Trigger>
              <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate></Setter.Value></Setter>
        </Style>
        """);

    private static Style TabControlStyle() => (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="TabControl">
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TabControl"><Grid>
            <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/></Grid.RowDefinitions>
            <Border BorderBrush="#343B3D" BorderThickness="0,0,0,1"><TabPanel IsItemsHost="True"/></Border>
            <ContentPresenter Grid.Row="1" ContentSource="SelectedContent"/>
          </Grid></ControlTemplate></Setter.Value></Setter>
        </Style>
        """);
    private static Style TabItemStyle() => (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="TabItem">
          <Setter Property="Foreground" Value="#A0ABAA"/><Setter Property="Padding" Value="15,12"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TabItem">
            <Border x:Name="Tab" Background="Transparent" BorderThickness="0,0,0,2" BorderBrush="Transparent" Padding="{TemplateBinding Padding}">
              <ContentPresenter ContentSource="Header" RecognizesAccessKey="True"/>
            </Border><ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Tab" Property="Background" Value="#262D2E"/></Trigger>
              <Trigger Property="IsSelected" Value="True"><Setter Property="Foreground" Value="#D7E8E0"/><Setter TargetName="Tab" Property="BorderBrush" Value="#A9C8BD"/></Trigger>
              <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="Tab" Property="Background" Value="#303B36"/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate></Setter.Value></Setter>
        </Style>
        """);
    private static Style ChoiceStyle() => (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ComboBox">
          <Setter Property="Foreground" Value="#E5E9E8"/><Setter Property="FontSize" Value="12"/>
          <Setter Property="ItemContainerStyle"><Setter.Value><Style TargetType="ComboBoxItem">
            <Setter Property="Padding" Value="10,8"/><Setter Property="Foreground" Value="#E5E9E8"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBoxItem">
              <Border x:Name="Item" Background="#252A2D" Padding="{TemplateBinding Padding}"><ContentPresenter/></Border>
              <ControlTemplate.Triggers><Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Item" Property="Background" Value="#3A4642"/></Trigger></ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter></Style></Setter.Value></Setter>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBox"><Grid>
            <ToggleButton Focusable="False" ClickMode="Press" IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
              <ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border Background="#252A2D" BorderBrush="#41494B" BorderThickness="1" CornerRadius="4">
                <TextBlock Text="⌄" Foreground="#A9C8BD" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,11,0"/>
              </Border></ControlTemplate></ToggleButton.Template>
            </ToggleButton>
            <ContentPresenter IsHitTestVisible="False" Margin="11,0,30,0" VerticalAlignment="Center" Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"/>
            <Popup x:Name="PART_Popup" Placement="Bottom" IsOpen="{TemplateBinding IsDropDownOpen}" AllowsTransparency="True" Focusable="False">
              <Border Background="#252A2D" BorderBrush="#596B62" BorderThickness="1" MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}">
                <ScrollViewer MaxHeight="420" CanContentScroll="True" VerticalScrollBarVisibility="Auto"><ItemsPresenter/></ScrollViewer>
              </Border>
            </Popup>
          </Grid></ControlTemplate></Setter.Value></Setter>
        </Style>
        """);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
