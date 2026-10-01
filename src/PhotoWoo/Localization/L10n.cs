using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace PhotoWoo.Localization;

public static class L10n
{
    private static readonly CultureInfo SystemCulture = CultureInfo.CurrentUICulture;
    private static readonly Dictionary<string, Dictionary<string, string>> Translations = SettingsTranslations.Create();
    public static IReadOnlyList<LanguageOption> AvailableLanguages { get; } =
    [
        new("auto", ""), new("ru", "Русский"), new("en", "English"), new("de", "Deutsch"),
        new("fr", "Français"), new("es", "Español"), new("it", "Italiano"),
        new("pt", "Português"), new("pl", "Polski"), new("uk", "Українська"), new("zh-Hans", "简体中文")
    ];

    public static string CurrentLanguage { get; private set; } = "en";
    public static string RequestedLanguage { get; private set; } = "auto";
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en");
    public static event EventHandler? Changed;

    static L10n() => SetLanguage("auto");

    public static string Text(string key)
    {
        if (Translations.TryGetValue(CurrentLanguage, out var current) && current.TryGetValue(key, out var text)) return text;
        return Translations.TryGetValue("en", out var fallback) && fallback.TryGetValue(key, out text) ? text : key;
    }

    public static string Format(string key, params object?[] args) => string.Format(Culture, Text(key), args);

    /// <summary>Register other UI domains before calling SetLanguage to publish their dynamic resources.</summary>
    public static void RegisterTranslations(string language, IReadOnlyDictionary<string, string> entries)
    {
        if (!Translations.TryGetValue(language, out var target)) Translations[language] = target = new(StringComparer.Ordinal);
        foreach (var entry in entries) target[entry.Key] = entry.Value;
    }

    public static void SetLanguage(string? language)
    {
        RequestedLanguage = string.IsNullOrWhiteSpace(language) ? "auto" : language;
        var candidate = RequestedLanguage == "auto" ? SystemCulture.Name : RequestedLanguage;
        CurrentLanguage = candidate.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-Hans"
            : AvailableLanguages.FirstOrDefault(item => item.Code != "auto" &&
                (candidate.Equals(item.Code, StringComparison.OrdinalIgnoreCase) || candidate.StartsWith(item.Code + "-", StringComparison.OrdinalIgnoreCase)))?.Code ?? "en";
        Culture = CultureInfo.GetCultureInfo(CurrentLanguage);
        CultureInfo.CurrentUICulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        if (Application.Current is { } application)
            foreach (var key in Translations.Values.SelectMany(dictionary => dictionary.Keys).Distinct(StringComparer.Ordinal))
                application.Resources["L10n." + key] = Text(key);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public sealed record LanguageOption(string Code, string NativeName)
    {
        public override string ToString() => Code == "auto" ? Text("settings.language.auto") : NativeName;
    }
}

[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new DynamicResourceExtension("L10n." + Key).ProvideValue(serviceProvider);
}
