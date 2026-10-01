using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PhotoWoo.Integration;

/// <summary>Offers PhotoWoo as a handler; Windows Settings remains responsible for the user's defaults.</summary>
public static class FileAssociations
{
    private const string ApplicationName = "PhotoWoo";
    private const string ClassesPath = @"Software\Classes";
    private const string ApplicationPath = ClassesPath + @"\Applications\PhotoWoo.exe";
    private const string CapabilitiesPath = @"Software\PhotoWoo\Capabilities";

    /// <summary>Call only from an explicit user action, never during application startup.</summary>
    public static void Register(string executablePath, string iconPath, IEnumerable<string> extensions)
    {
        var plan = CreateRegistrationPlan(executablePath, iconPath, extensions);
        if (!File.Exists(executablePath))
            throw new FileNotFoundException("Не найден исполняемый файл PhotoWoo.", executablePath);
        if (!File.Exists(iconPath))
            throw new FileNotFoundException("Не найдена иконка файлов PhotoWoo.", iconPath);

        // Every path is relative to HKCU. In particular, no extension default value,
        // Explorer FileExts\UserChoice key, or another application's ProgID is modified.
        foreach (var group in plan.GroupBy(entry => entry.KeyPath))
        {
            using var key = Registry.CurrentUser.CreateSubKey(group.Key, writable: true)
                ?? throw new IOException($"Не удалось зарегистрировать {ApplicationName}.");
            foreach (var entry in group)
                key.SetValue(entry.ValueName, entry.Value, RegistryValueKind.String);
        }

        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED / SHCNF_IDLIST
    }

    public static void OpenDefaultAppsSettings()
    {
        var uri = GetSettingsUri(Environment.OSVersion.Version);
        try
        {
            LaunchSettings(uri);
        }
        catch (Win32Exception) when (uri != "ms-settings:defaultapps")
        {
            LaunchSettings("ms-settings:defaultapps");
        }
    }

    // This pure plan is also used by the checks, so verifying the registration never writes the registry.
    internal static IReadOnlyList<RegistrationEntry> CreateRegistrationPlan(
        string executablePath, string iconPath, IEnumerable<string> extensions)
    {
        ValidatePath(executablePath, ".exe", nameof(executablePath));
        ValidatePath(iconPath, ".ico", nameof(iconPath));
        ArgumentNullException.ThrowIfNull(extensions);
        var normalizedExtensions = extensions.Select(NormalizeExtension)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (normalizedExtensions.Length == 0)
            throw new ArgumentException("Нужен хотя бы один формат изображения.", nameof(extensions));

        var executable = Path.GetFullPath(executablePath);
        var documentIcon = $"\"{Path.GetFullPath(iconPath)}\",0";
        var applicationIcon = $"\"{executable}\",0";
        var openCommand = $"\"{executable}\" \"%1\"";
        var entries = new List<RegistrationEntry>
        {
            new(ApplicationPath, "FriendlyAppName", ApplicationName),
            new(ApplicationPath + @"\DefaultIcon", "", applicationIcon),
            new(ApplicationPath + @"\shell\open", "MultiSelectModel", "Single"),
            new(ApplicationPath + @"\shell\open\command", "", openCommand),
            new(CapabilitiesPath, "ApplicationName", ApplicationName),
            new(CapabilitiesPath, "ApplicationDescription", "Быстрый просмотр изображений и RAW, поворот и печать."),
            new(CapabilitiesPath, "ApplicationIcon", applicationIcon)
        };

        foreach (var extension in normalizedExtensions)
        {
            // Keep this association version stable across ordinary application releases.
            var progId = $"PhotoWoo.Image.{extension[1..]}.1";
            var progIdPath = ClassesPath + @"\" + progId;
            var typeName = $"Изображение {extension[1..].ToUpperInvariant()} (PhotoWoo)";
            entries.AddRange([
                new(progIdPath, "", typeName),
                new(progIdPath, "FriendlyTypeName", typeName),
                new(progIdPath + @"\DefaultIcon", "", documentIcon),
                new(progIdPath + @"\shell", "", "open"),
                new(progIdPath + @"\shell\open", "MultiSelectModel", "Single"),
                new(progIdPath + @"\shell\open\command", "", openCommand),
                new(ClassesPath + @"\" + extension + @"\OpenWithProgids", progId, ""),
                new(ApplicationPath + @"\SupportedTypes", extension, ""),
                new(CapabilitiesPath + @"\FileAssociations", extension, progId)
            ]);
        }

        entries.Add(new(@"Software\RegisteredApplications", ApplicationName, CapabilitiesPath));
        return entries;
    }

    internal static string GetSettingsUri(Version windowsVersion) => windowsVersion >= new Version(10, 0, 22000)
        ? "ms-settings:defaultapps?registeredAppUser=" + Uri.EscapeDataString(ApplicationName)
        : "ms-settings:defaultapps";

    private static void LaunchSettings(string uri) =>
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });

    private static void ValidatePath(string path, string extension, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (!Path.IsPathFullyQualified(path) || path.Contains('"') || path.IndexOf('\0') >= 0 ||
            !Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Нужен полный путь к файлу {extension}.", parameterName);
    }

    private static string NormalizeExtension(string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        var normalized = extension.Trim().ToLowerInvariant();
        if (normalized.Length is < 2 or > 17 || normalized[0] != '.' ||
            normalized.AsSpan(1).ContainsAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789"))
            throw new ArgumentException($"Неверное расширение изображения: {extension}", nameof(extension));
        return normalized;
    }

    internal sealed record RegistrationEntry(string KeyPath, string ValueName, string Value);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
