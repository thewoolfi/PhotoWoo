namespace PhotoWoo.Updates;

public sealed record UpdateRelease(Version Version, string TagName, Uri ReleasePage,
    Uri AssetUrl, long AssetSize, string? Sha256, Uri? ChecksumUrl, string ReleaseNotes = "");

public sealed record UpdateProgress(string Phase, long BytesReceived, long? TotalBytes)
{
    public double? Percentage => TotalBytes is > 0
        ? Math.Clamp(BytesReceived * 100d / TotalBytes.Value, 0, 100) : null;
}

public sealed record PreparedUpdate(string RootDirectory, string PayloadDirectory,
    UpdateRelease Release, string Sha256);
