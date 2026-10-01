using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhotoWoo.Updates;

public sealed class UpdateService : IDisposable
{
    internal const long MaxArchiveBytes = 512L * 1024 * 1024;
    internal const long MaxExpandedBytes = 1536L * 1024 * 1024;
    internal const int MaxEntries = 5000;
    private const string Repository = "https://github.com/thewoolfi/PhotoWoo";
    private static readonly Uri Feed = new("https://api.github.com/repos/thewoolfi/PhotoWoo/releases/latest");
    private static readonly Regex VersionPattern = new(@"\Av(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant);
    private static readonly Regex HashPattern = new(@"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant);
    private static readonly Regex DevicePattern = new(@"\A(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|CLOCK\$|COM[1-9¹²³]|LPT[1-9¹²³])\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly string[] RequiredFiles = ["PhotoWoo.exe", "PhotoWoo.dll", "PhotoWoo.deps.json",
        "PhotoWoo.runtimeconfig.json", "Assets/PhotoWoo.ImageFile.ico", "Assets/PhotoWoo.ImageFile.png"];
    private readonly HttpClient _http;
    private readonly string _updatesDirectory;

    public UpdateService() : this(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(15)
    }, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoWoo", "Updates")) { }

    internal UpdateService(HttpMessageHandler handler, string updatesDirectory)
    {
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PhotoWoo-Updater/1.0");
        _updatesDirectory = Path.GetFullPath(updatesDirectory).TrimEnd(Path.DirectorySeparatorChar);
    }

    public async Task<UpdateRelease?> CheckAsync(Version current, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(current);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await GetAsync(Feed, "application/vnd.github+json", timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            var data = await ReadSmallAsync(response, 1024 * 1024, timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (RequiredBoolean(root, "draft") || RequiredBoolean(root, "prerelease")) return null;
            var tag = RequiredString(root, "tag_name");
            var version = ParseTag(tag);
            if (version <= new Version(current.Major, current.Minor, Math.Max(0, current.Build))) return null;
            var page = ExactUrl(RequiredString(root, "html_url"), $"{Repository}/releases/tag/{tag}");
            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array || assets.GetArrayLength() > 1000)
                throw new InvalidDataException("The release has no valid asset list.");
            var filename = ArchiveName(version);
            var asset = FindAsset(assets, filename) ?? throw new InvalidDataException("The Windows update archive is missing.");
            var url = ExactUrl(RequiredString(asset, "browser_download_url"), AssetAddress(tag, filename));
            if (!asset.TryGetProperty("size", out var sizeElement) || !sizeElement.TryGetInt64(out var size) || size <= 0 || size > MaxArchiveBytes)
                throw new InvalidDataException("The update archive size is invalid.");
            string? sha256 = null;
            if (asset.TryGetProperty("digest", out var digest) && digest.ValueKind != JsonValueKind.Null)
            {
                var value = digest.GetString();
                if (value is null || !value.StartsWith("sha256:", StringComparison.Ordinal) || !HashPattern.IsMatch(value[7..]))
                    throw new InvalidDataException("The release checksum is invalid.");
                sha256 = value[7..].ToUpperInvariant();
            }
            Uri? checksumUrl = null;
            if (sha256 is null)
            {
                var checksum = FindAsset(assets, filename + ".sha256")
                    ?? throw new InvalidDataException("The release has no SHA-256 verification data.");
                checksumUrl = ExactUrl(RequiredString(checksum, "browser_download_url"), AssetAddress(tag, filename + ".sha256"));
                if (!checksum.TryGetProperty("size", out var checksumSize) || !checksumSize.TryGetInt64(out var bytes) || bytes <= 0 || bytes > 4096)
                    throw new InvalidDataException("The checksum file is too large or empty.");
            }
            var notes = root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() ?? "" : "";
            notes = new string(notes.Take(16000).Where(c => !char.IsControl(c) || c is '\r' or '\n' or '\t').ToArray());
            return new UpdateRelease(version, tag, page, url, size, sha256, checksumUrl, notes);
        }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
        { throw new TimeoutException("Checking for updates timed out.", ex); }
        catch (JsonException ex) { throw new InvalidDataException("The release response is invalid.", ex); }
        catch (InvalidOperationException ex) { throw new InvalidDataException("The release response has invalid field types.", ex); }
    }

    public async Task<PreparedUpdate> PrepareAsync(UpdateRelease release, IProgress<UpdateProgress>? progress, CancellationToken token)
    {
        ValidateRelease(release);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var ct = timeout.Token;
        string? stage = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var expectedHash = release.Sha256 ?? await ReadChecksumAsync(release, ct).ConfigureAwait(false);
            EnsureNoReparsePoints(_updatesDirectory);
            Directory.CreateDirectory(_updatesDirectory);
            EnsureNoReparsePoints(_updatesDirectory);
            stage = Path.Combine(_updatesDirectory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            EnsureNoReparsePoints(stage);
            var archive = Path.Combine(stage, "package.zip");
            var actualHash = await DownloadAsync(release, archive, progress, ct).ConfigureAwait(false);
            progress?.Report(new UpdateProgress("verifying", release.AssetSize, release.AssetSize));
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedHash), Convert.FromHexString(actualHash)))
                throw new InvalidDataException("The downloaded update failed SHA-256 verification.");
            var payload = Path.Combine(stage, "payload");
            await Task.Run(() => ExtractAsync(archive, payload, release.Version, progress, ct), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            EnsureNoReparsePoints(stage);
            await using (var metadata = new FileStream(Path.Combine(stage, "update.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(metadata, new
                {
                    schemaVersion = 1, version = release.Version.ToString(3), tag = release.TagName,
                    assetUrl = release.AssetUrl.AbsoluteUri, sha256 = actualHash, archiveFile = "package.zip",
                    payloadDirectory = "payload", preparedAtUtc = DateTimeOffset.UtcNow
                }, cancellationToken: ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            progress?.Report(new UpdateProgress("ready", release.AssetSize, release.AssetSize));
            return new PreparedUpdate(stage, payload, release, actualHash);
        }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            TryDeleteStage(stage);
            throw new TimeoutException("Preparing the update timed out.", ex);
        }
        catch { TryDeleteStage(stage); throw; }
    }

    public void CleanupPrepared(PreparedUpdate prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (Path.GetFullPath(prepared.PayloadDirectory) != Path.Combine(Path.GetFullPath(prepared.RootDirectory), "payload")) return;
        TryDeleteStage(prepared.RootDirectory);
    }

    private async Task<string> ReadChecksumAsync(UpdateRelease release, CancellationToken token)
    {
        using var response = await GetAsync(release.ChecksumUrl!, "application/octet-stream", token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var text = Encoding.UTF8.GetString(await ReadSmallAsync(response, 4096, token).ConfigureAwait(false)).Trim().TrimStart('\uFEFF');
        var match = Regex.Match(text, @"\A(?<hash>[a-fA-F0-9]{64})(?:[ \t]+\*?(?<name>[^\r\n]+))?\z", RegexOptions.CultureInvariant);
        if (!match.Success || (match.Groups["name"].Success && match.Groups["name"].Value != ArchiveName(release.Version)))
            throw new InvalidDataException("The checksum file is invalid or names a different archive.");
        return match.Groups["hash"].Value.ToUpperInvariant();
    }

    private async Task<string> DownloadAsync(UpdateRelease release, string archive, IProgress<UpdateProgress>? progress, CancellationToken token)
    {
        using var response = await GetAsync(release.AssetUrl, "application/octet-stream", token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long declared && declared != release.AssetSize)
            throw new InvalidDataException("The update download size does not match the release.");
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long total = 0;
        var lastReport = Stopwatch.StartNew();
        progress?.Report(new UpdateProgress("downloading", 0, release.AssetSize));
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, 64 * 1024), token).ConfigureAwait(false)) != 0)
            {
                total += read;
                if (total > release.AssetSize || total > MaxArchiveBytes) throw new InvalidDataException("The update download is too large.");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                if (lastReport.ElapsedMilliseconds >= 100)
                {
                    progress?.Report(new UpdateProgress("downloading", total, release.AssetSize));
                    lastReport.Restart();
                }
            }
            if (total != release.AssetSize) throw new InvalidDataException("The update download is incomplete.");
            await output.FlushAsync(token).ConfigureAwait(false);
            progress?.Report(new UpdateProgress("downloading", total, release.AssetSize));
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private async Task<HttpResponseMessage> GetAsync(Uri address, string accept, CancellationToken token)
    {
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (!AllowedAddress(address)) throw new InvalidDataException("The update server redirected to an untrusted address.");
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.Accept.ParseAdd(accept);
            if (address.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is Uri actual && !AllowedAddress(actual))
            {
                response.Dispose();
                throw new InvalidDataException("The update response came from an untrusted address.");
            }
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null || redirects == 5) throw new InvalidDataException("The update download has an invalid redirect chain.");
            address = location.IsAbsoluteUri ? location : new Uri(address, location);
        }
        throw new InvalidDataException("The update download has too many redirects.");
    }

    private static bool AllowedAddress(Uri address) => address.IsAbsoluteUri && address.Scheme == Uri.UriSchemeHttps
        && address.Port == 443 && address.UserInfo.Length == 0 && address.Fragment.Length == 0
        && address.IdnHost.ToLowerInvariant() is "github.com" or "api.github.com" or "release-assets.githubusercontent.com"
            or "objects.githubusercontent.com" or "github-releases.githubusercontent.com";

    private static async Task<byte[]> ReadSmallAsync(HttpResponseMessage response, int limit, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength is long size && size > limit) throw new InvalidDataException("The update response is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > limit) throw new InvalidDataException("The update response is too large.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static async Task ExtractAsync(string archivePath, string payload, Version version, IProgress<UpdateProgress>? progress, CancellationToken token)
    {
        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var expectedCount = CheckZipDirectory(stream);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count != expectedCount) throw new InvalidDataException("The ZIP entry count is inconsistent.");
        var plans = new List<(ZipArchiveEntry Entry, string Path, bool Directory)>();
        var declaredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var originalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prefix = $"PhotoWoo-{version.ToString(3)}/";
        var hasPrefix = archive.Entries.Any(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal));
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var name = entry.FullName;
            ValidateEntryName(name);
            if (!originalPaths.Add(name.TrimEnd('/'))) throw new InvalidDataException("The ZIP has duplicate Windows paths.");
            var mode = (entry.ExternalAttributes >> 16) & 0xF000;
            if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || mode is not (0 or 0x8000 or 0x4000))
                throw new InvalidDataException("The ZIP contains a link or special filesystem entry.");
            var directory = name.EndsWith('/');
            if (!directory && (mode == 0x4000 || (entry.ExternalAttributes & (int)FileAttributes.Directory) != 0))
                throw new InvalidDataException("The ZIP directory metadata is inconsistent.");
            if (directory && entry.Length != 0) throw new InvalidDataException("The ZIP has a directory with file content.");
            if (hasPrefix)
            {
                if (!name.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("The ZIP mixes incompatible directory layouts.");
                name = name[prefix.Length..];
                if (name.Length == 0) continue;
            }
            name = name.TrimEnd('/');
            if (!declaredPaths.Add(name)) throw new InvalidDataException("The ZIP has duplicate Windows paths.");
            if (directory)
            {
                if (files.Contains(name)) throw new InvalidDataException("The ZIP has conflicting file and directory paths.");
                directories.Add(name);
            }
            else
            {
                if (directories.Contains(name)) throw new InvalidDataException("The ZIP has conflicting file and directory paths.");
                files.Add(name);
            }
            var parent = name.LastIndexOf('/');
            while (parent > 0)
            {
                var parentName = name[..parent];
                if (files.Contains(parentName)) throw new InvalidDataException("The ZIP has conflicting file and directory paths.");
                directories.Add(parentName);
                parent = parentName.LastIndexOf('/');
            }
            if (entry.Length < 0 || entry.Length > MaxExpandedBytes - expanded) throw new InvalidDataException("The expanded update is too large.");
            expanded += entry.Length;
            plans.Add((entry, name, directory));
        }
        if (RequiredFiles.Any(file => !files.Contains(file))) throw new InvalidDataException("The update is missing required application files.");
        EnsureNoReparsePoints(payload);
        Directory.CreateDirectory(payload);
        var payloadPrefix = Path.GetFullPath(payload) + Path.DirectorySeparatorChar;
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long written = 0;
        var reportWatch = Stopwatch.StartNew();
        progress?.Report(new UpdateProgress("extracting", 0, expanded));
        try
        {
            foreach (var item in plans)
            {
                token.ThrowIfCancellationRequested();
                var destination = Path.GetFullPath(Path.Combine(payload, item.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(payloadPrefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The ZIP path escapes its destination.");
                EnsureNoReparsePoints(destination);
                if (item.Directory) { Directory.CreateDirectory(destination); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                EnsureNoReparsePoints(destination);
                using var input = item.Entry.Open();
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                long entryWritten = 0;
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, 64 * 1024), token).ConfigureAwait(false)) > 0)
                {
                    entryWritten += read;
                    written += read;
                    if (entryWritten > item.Entry.Length || written > MaxExpandedBytes) throw new InvalidDataException("The expanded update exceeded its declared size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    if (reportWatch.ElapsedMilliseconds >= 100)
                    {
                        progress?.Report(new UpdateProgress("extracting", written, expanded));
                        reportWatch.Restart();
                    }
                }
                if (entryWritten != item.Entry.Length) throw new InvalidDataException("An update file is incomplete.");
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        foreach (var required in RequiredFiles)
        {
            var path = Path.Combine(payload, required);
            EnsureNoReparsePoints(path);
            if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new InvalidDataException("An application file is missing or empty.");
        }
        try
        {
            var assembly = AssemblyName.GetAssemblyName(Path.Combine(payload, "PhotoWoo.dll"));
            if (assembly.Name != "PhotoWoo" || assembly.Version is not Version actual || actual.Major != version.Major
                || actual.Minor != version.Minor || actual.Build != version.Build || actual.Revision is not (0 or -1))
                throw new InvalidDataException("The application version does not match the release.");
        }
        catch (BadImageFormatException ex) { throw new InvalidDataException("The update application assembly is invalid.", ex); }
        progress?.Report(new UpdateProgress("extracting", expanded, expanded));
    }

    private static int CheckZipDirectory(FileStream stream)
    {
        if (stream.Length < 22 || stream.Length > MaxArchiveBytes) throw new InvalidDataException("The update is not a valid bounded ZIP archive.");
        var tail = new byte[(int)Math.Min(stream.Length, 65557)];
        stream.Position = stream.Length - tail.Length;
        stream.ReadExactly(tail);
        for (var i = tail.Length - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) != 0x06054B50) continue;
            var end = tail.AsSpan(i);
            if (i + 22 + BinaryPrimitives.ReadUInt16LittleEndian(end[20..]) != tail.Length) continue;
            var entries = BinaryPrimitives.ReadUInt16LittleEndian(end[10..]);
            var bytes = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(end[16..]);
            if (BinaryPrimitives.ReadUInt32LittleEndian(end[4..]) != 0 || entries == 0 || entries > MaxEntries
                || BinaryPrimitives.ReadUInt16LittleEndian(end[8..]) != entries || bytes > 16 * 1024 * 1024
                || (long)offset + bytes > stream.Length - tail.Length + i)
                throw new InvalidDataException("The ZIP directory is too large, split across volumes, or invalid.");
            stream.Position = 0;
            return entries;
        }
        throw new InvalidDataException("The ZIP directory is missing or invalid.");
    }

    private static void ValidateEntryName(string name)
    {
        if (name.Length == 0 || name.Length > 512 || name.Contains('\\') || name.StartsWith('/') || Path.IsPathRooted(name))
            throw new InvalidDataException("The ZIP contains an invalid Windows path.");
        var path = name.EndsWith('/') ? name[..^1] : name;
        foreach (var part in path.Split('/'))
        {
            if (part.Length == 0 || part.Length > 255 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || part.Any(c => char.IsControl(c) || "<>:\"|?*".Contains(c)) || DevicePattern.IsMatch(part.Split('.')[0]))
                throw new InvalidDataException("The ZIP contains an unsafe Windows filename.");
        }
    }

    private static void ValidateRelease(UpdateRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        var version = ParseTag(release.TagName);
        if (version != release.Version) throw new InvalidDataException("The release version and tag differ.");
        ExactUrl(release.ReleasePage.OriginalString, $"{Repository}/releases/tag/{release.TagName}");
        ExactUrl(release.AssetUrl.OriginalString, AssetAddress(release.TagName, ArchiveName(version)));
        if (release.AssetSize <= 0 || release.AssetSize > MaxArchiveBytes) throw new InvalidDataException("The update archive size is invalid.");
        if (release.Sha256 is not null && !HashPattern.IsMatch(release.Sha256)) throw new InvalidDataException("The release checksum is invalid.");
        if (release.Sha256 is null && release.ChecksumUrl is null) throw new InvalidDataException("The release has no SHA-256 verification data.");
        if (release.ChecksumUrl is not null)
            ExactUrl(release.ChecksumUrl.OriginalString, AssetAddress(release.TagName, ArchiveName(version) + ".sha256"));
    }

    private static Version ParseTag(string tag)
    {
        if (tag.Length > 40 || !VersionPattern.IsMatch(tag) || !Version.TryParse(tag[1..], out var version) || version.Build < 0 || version.Revision != -1)
            throw new InvalidDataException("The release tag must have the form v1.2.3.");
        return version;
    }

    private static Uri ExactUrl(string value, string expected)
    {
        if (!string.Equals(value, expected, StringComparison.Ordinal) || !Uri.TryCreate(value, UriKind.Absolute, out var address) || !AllowedAddress(address))
            throw new InvalidDataException("The update URL is not an official release address.");
        return address;
    }

    private static string ArchiveName(Version version) => $"PhotoWoo-{version.ToString(3)}-win-x64.zip";
    private static string AssetAddress(string tag, string name) => $"{Repository}/releases/download/{tag}/{name}";

    private static JsonElement? FindAsset(JsonElement assets, string name)
    {
        JsonElement? found = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var value) || value.GetString() != name) continue;
            if (found is not null || RequiredString(asset, "state") != "uploaded") throw new InvalidDataException("The release asset is duplicated or incomplete.");
            found = asset;
        }
        return found;
    }

    private static string RequiredString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"The release field '{name}' is invalid.");
        return value.GetString()!;
    }

    private static bool RequiredBoolean(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"The release field '{name}' is invalid.");
        return value.GetBoolean();
    }

    private static void EnsureNoReparsePoints(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The update staging path contains a filesystem link.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private void TryDeleteStage(string? stage)
    {
        if (stage is null) return;
        try
        {
            if (Path.GetDirectoryName(Path.GetFullPath(stage)) != _updatesDirectory || !Guid.TryParseExact(Path.GetFileName(stage), "N", out _)) return;
            EnsureNoReparsePoints(_updatesDirectory);
            DeleteTreeWithoutFollowingLinks(stage);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void DeleteTreeWithoutFollowingLinks(string directory)
    {
        var attributes = File.GetAttributes(directory);
        if ((attributes & FileAttributes.ReparsePoint) == 0)
        {
            foreach (var item in Directory.EnumerateFileSystemEntries(directory))
            {
                var itemAttributes = File.GetAttributes(item);
                if ((itemAttributes & FileAttributes.Directory) != 0) DeleteTreeWithoutFollowingLinks(item);
                else File.Delete(item);
            }
        }
        Directory.Delete(directory);
    }

    public void Dispose() => _http.Dispose();
}
