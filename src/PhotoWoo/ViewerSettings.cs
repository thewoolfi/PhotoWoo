namespace PhotoWoo;

public sealed class ViewerSettings
{
    public const string DefaultSupportUrl = "https://boosty.to/andrewwoolfi";
    public bool Filmstrip { get; set; }
    public string Language { get; set; } = "auto";
    public bool Animations { get; set; } = true;
    public bool Inertia { get; set; } = true;
    public bool FullscreenFill { get; set; }
    public double FullscreenHideDelaySeconds { get; set; } = 2;
    public string SupportUrl { get; set; } = DefaultSupportUrl;
    public bool CheckUpdatesAutomatically { get; set; } = true;

    public ViewerSettings Copy() => (ViewerSettings)MemberwiseClone();

    public static bool IsValidSupportUrl(string? url) => !string.IsNullOrWhiteSpace(url) && TryNormalizeSupportUrl(url, out _);

    public static bool TryNormalizeSupportUrl(string? text, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length != 0 || !uri.IsDefaultPort || string.IsNullOrWhiteSpace(uri.AbsolutePath.Trim('/')) ||
            !new[] { "ko-fi.com", "boosty.to", "donationalerts.com", "www.donationalerts.com", "donatty.com" }
                .Contains(uri.Host, StringComparer.OrdinalIgnoreCase)) return false;
        normalized = uri.AbsoluteUri;
        return true;
    }
}
