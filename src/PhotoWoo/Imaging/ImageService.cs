using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;
using ImageMagick;
using ImageMagick.Formats;

namespace PhotoWoo.Imaging;

/// <summary>
/// A disk-to-bitmap decoder with no permanent pixel cache. The caller owns its display/cache budget.
/// RAW previews use the camera's embedded image; full-resolution viewing and saving always develop
/// the sensor data. A preview and a developed RAW can therefore differ in colour and crop.
/// </summary>
public sealed class ImageService
{
    private static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".3fr", ".arw", ".bay", ".bmq", ".cap", ".cr2", ".cr3", ".crw", ".cs1", ".dcs", ".dcr",
        ".dng", ".drf", ".erf", ".fff", ".gpr", ".iiq", ".k25", ".kdc", ".mdc",
        ".mef", ".mos", ".mrw", ".nef", ".nrw", ".obm", ".orf", ".pef", ".ptx", ".pxn",
        ".raf", ".raw", ".rdc", ".rw2", ".rwl", ".sr2", ".srf", ".srw", ".x3f"
    };

    private static readonly HashSet<string> RasterExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".apng", ".webp", ".tif", ".tiff", ".bmp",
        ".dib", ".gif", ".heic", ".heif", ".avif", ".jxl", ".jp2", ".j2k", ".jpf", ".jpx",
        ".psd", ".psb", ".tga", ".dds", ".ico", ".exr", ".hdr", ".pfm", ".ppm", ".pgm", ".pbm",
        ".jif", ".jfi", ".pjpeg", ".pjp", ".jps", ".jpc", ".j2c", ".jpm", ".jpt",
        ".jxr", ".wdp", ".hdp", ".hif", ".avifs", ".heics", ".heifs",
        ".svg", ".svgz", ".cur", ".ani", ".icns", ".icon", ".icn", ".pcx", ".dcx", ".pcd", ".pcds",
        ".pct", ".pict", ".pic", ".targa", ".icb", ".vda", ".vst", ".rle",
        ".pnm", ".pam", ".pgx", ".phm", ".qoi", ".jng", ".mng", ".wbmp",
        ".ras", ".sun", ".sgi", ".rgb", ".rgba", ".bw", ".int", ".inta",
        ".xbm", ".xpm", ".xcf", ".ora", ".kra", ".emf", ".wmf",
        ".dpx", ".cin", ".fits", ".fit", ".fts", ".dcm", ".dicom", ".miff", ".mif",
        ".ff", ".farbfeld", ".cut", ".art", ".sct", ".wpg"
    };

    private static readonly HashSet<string> WicExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".jpe", ".jfif", ".jif", ".jfi", ".pjpeg", ".pjp", ".jps", ".png", ".bmp", ".dib", ".gif", ".tif", ".tiff", ".jxr", ".wdp", ".hdp" };

    // Limit native decoders, including background thumbnail requests, across all service instances.
    private static readonly SemaphoreSlim DecodeSlots = new(2, 2);
    private static readonly Lazy<bool> NativeLimits = new(() =>
    {
        ResourceLimits.Memory = 512UL * 1024 * 1024;
        ResourceLimits.Disk = 4UL * 1024 * 1024 * 1024;
        ResourceLimits.Thread = (ulong)Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        return true;
    });

    public static bool IsRaw(string path) => RawExtensions.Contains(Path.GetExtension(path));
    internal static void EnsureNativeLimits() => _ = NativeLimits.Value;

    /// <summary>Recognised candidate extensions; actual decoding depends on file and camera support.</summary>
    public static bool IsSupported(string path) => IsRaw(path) || RasterExtensions.Contains(Path.GetExtension(path));

    public static IReadOnlyList<string> SupportedExtensions { get; } = Array.AsReadOnly(
        RasterExtensions.Concat(RawExtensions).Order(StringComparer.OrdinalIgnoreCase).ToArray());

    public static string OpenFilter { get; } = "Изображения и RAW|" +
        string.Join(';', SupportedExtensions.Select(extension => "*" + extension)) +
        "|Все файлы|*.*";

    public async Task<LoadedImage> LoadAsync(string path, int maxDimension, bool fullResolution,
        CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maxDimension < 1)
            throw new ArgumentOutOfRangeException(nameof(maxDimension));

        await DecodeSlots.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Load(path, maxDimension, fullResolution, token), token)
                .ConfigureAwait(false);
        }
        finally
        {
            DecodeSlots.Release();
        }
    }

    /// <summary>
    /// Saves the first image (or the best ICO frame) at full resolution after orientation and turns.
    /// JPEG is recompressed. RAW originals and multi-frame originals cannot be replaced in place.
    /// A new copy of an animation or layered document contains its first frame only.
    /// </summary>
    public async Task SaveRotatedAsync(string source, string destination, int quarterTurns,
        CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var sourcePath = Path.GetFullPath(source);
        var destinationPath = Path.GetFullPath(destination);
        var sameFile = string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase);
        if (sameFile && IsRaw(sourcePath))
            throw new InvalidOperationException("RAW сохраняется отдельной копией: выберите JPEG, PNG, TIFF или WebP.");
        var format = GetOutputFormat(destinationPath);

        await DecodeSlots.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await Task.Run(() => Save(sourcePath, destinationPath, quarterTurns, sameFile, format, token), token)
                .ConfigureAwait(false);
        }
        finally
        {
            DecodeSlots.Release();
        }
    }

    private static LoadedImage Load(string path, int maxDimension, bool fullResolution, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var watch = Stopwatch.StartNew();
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".ico" or ".icon" or ".icn" or ".cur" or ".ani")
        {
            using var stream = ExtraImageFormats.OpenIcon(path);
            var icon = IconImageDecoder.Decode(stream, maxDimension, fullResolution, token);
            return new LoadedImage(icon.Bitmap, icon.Width, icon.Height,
                icon.Bitmap.PixelWidth != icon.Width || icon.Bitmap.PixelHeight != icon.Height, extension[1..].ToUpperInvariant(), watch.Elapsed);
        }
        if (WicExtensions.Contains(Path.GetExtension(path)))
        {
            try
            {
                return LoadWithWic(path, maxDimension, fullResolution, watch, token);
            }
            catch (Exception error) when (error is NotSupportedException or FileFormatException or COMException
                                          or ArgumentException or InvalidOperationException)
            {
                // Unusual bit depth, profiles or compression may still be supported by ImageMagick.
                token.ThrowIfCancellationRequested();
            }
        }

        _ = NativeLimits.Value;
        var raw = IsRaw(path);
        if (raw && !fullResolution)
        {
            var preview = TryLoadRawPreview(path, maxDimension, watch, token);
            if (preview is not null)
                return preview;
        }

        using var image = NewImage(token);
        var settings = CreateReadSettings(raw);
        ExtraImageFormats.SetReadFormat(path, settings);
        try
        {
            // Streams keep file names literal: brackets and coder-like prefixes are not interpreted.
            using var stream = ExtraImageFormats.OpenImage(path);
            ExtraImageFormats.Read(image, stream, settings, path);
            token.ThrowIfCancellationRequested();
            image.AutoOrient();
            var width = checked((int)image.Width);
            var height = checked((int)image.Height);
            var format = raw ? Path.GetExtension(path).TrimStart('.').ToUpperInvariant() : image.Format.ToString().ToUpperInvariant();
            if (!fullResolution)
                ResizeForDisplay(image, maxDimension);
            var bitmap = ToBitmap(image, token);
            return new LoadedImage(bitmap, width, height,
                image.Width != width || image.Height != height, format, watch.Elapsed);
        }
        catch (MagickException) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
    }

    private static LoadedImage LoadWithWic(string path, int maxDimension, bool fullResolution,
        Stopwatch watch, CancellationToken token)
    {
        using var stream = OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        var width = frame.PixelWidth;
        var height = frame.PixelHeight;
        var orientation = ReadWicOrientation(frame);
        token.ThrowIfCancellationRequested();
        stream.Position = 0;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        // StreamSource is not URI-cached. IgnoreImageCache with a null UriSource makes WPF throw.
        bitmap.CreateOptions = BitmapCreateOptions.None;
        bitmap.StreamSource = stream;
        if (!fullResolution && Math.Max(width, height) > maxDimension)
        {
            if (width >= height)
                bitmap.DecodePixelWidth = maxDimension;
            else
                bitmap.DecodePixelHeight = maxDimension;
        }
        bitmap.EndInit();
        bitmap.Freeze();
        token.ThrowIfCancellationRequested();

        BitmapSource result = bitmap;
        var matrix = orientation switch
        {
            2 => new Matrix(-1, 0, 0, 1, 0, 0),
            3 => new Matrix(-1, 0, 0, -1, 0, 0),
            4 => new Matrix(1, 0, 0, -1, 0, 0),
            5 => new Matrix(0, 1, 1, 0, 0, 0),
            6 => new Matrix(0, 1, -1, 0, 0, 0),
            7 => new Matrix(0, -1, -1, 0, 0, 0),
            8 => new Matrix(0, -1, 1, 0, 0, 0),
            _ => Matrix.Identity
        };
        if (!matrix.IsIdentity)
        {
            result = new TransformedBitmap(bitmap, new MatrixTransform(matrix));
            result.Freeze();
        }
        if (orientation >= 5)
            (width, height) = (height, width);
        return new LoadedImage(result, width, height, result.PixelWidth != width || result.PixelHeight != height,
            Path.GetExtension(path).TrimStart('.').ToUpperInvariant(), watch.Elapsed);
    }

    private static ushort ReadWicOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata)
            {
                foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
                {
                    if (metadata.ContainsQuery(query) && metadata.GetQuery(query) is ushort value && value is >= 1 and <= 8)
                        return value;
                }
            }
        }
        catch (Exception error) when (error is NotSupportedException or FileFormatException or COMException)
        {
            // Missing or unsupported metadata must not prevent displaying otherwise valid pixels.
        }
        return 1;
    }

    private static LoadedImage? TryLoadRawPreview(string path, int maxDimension, Stopwatch watch,
        CancellationToken token)
    {
        try
        {
            using var metadata = NewImage(token);
            var settings = CreateReadSettings(true);
            settings.SetDefines(new DngReadDefines { ReadThumbnail = true });
            using (var stream = OpenRead(path))
                metadata.Ping(stream, settings);
            token.ThrowIfCancellationRequested();
            var profile = metadata.GetProfile("dng:thumbnail");
            if (profile is null)
                return null;

            using var preview = NewImage(token);
            // Most cameras embed a JPEG. Unencoded bitmap thumbnails fall back to sensor decoding.
            preview.Read(profile.ToByteArray());
            if (preview.Orientation == OrientationType.Undefined)
                preview.Orientation = metadata.Orientation;
            preview.AutoOrient();
            var width = checked((int)metadata.Width);
            var height = checked((int)metadata.Height);
            if ((int)metadata.Orientation >= 5)
                (width, height) = (height, width);
            ResizeForDisplay(preview, maxDimension);
            return new LoadedImage(ToBitmap(preview, token), width, height, true,
                Path.GetExtension(path).TrimStart('.').ToUpperInvariant(), watch.Elapsed);
        }
        catch (MagickException) when (!token.IsCancellationRequested)
        {
            return null; // A missing/broken thumbnail does not make the RAW itself unreadable.
        }
        catch (MagickException) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
    }

    private static MagickReadSettings CreateReadSettings(bool raw)
    {
        var settings = new MagickReadSettings { FrameIndex = 0, FrameCount = 1 };
        if (raw)
        {
            // RAW TIFF containers often expose a tiny JPEG to a generic TIFF decoder. Force LibRaw,
            // including for stream inputs where the original camera file extension is unavailable.
            settings.Format = MagickFormat.Dng;
            settings.SetDefines(new DngReadDefines
            {
                ReadThumbnail = false,
                UseCameraWhiteBalance = true,
                OutputColor = DngOutputColor.SRGB
            });
        }
        return settings;
    }

    private static MagickImage NewImage(CancellationToken token)
    {
        var image = new MagickImage();
        image.Progress += (_, progress) => progress.Cancel = token.IsCancellationRequested;
        return image;
    }

    private static void ResizeForDisplay(MagickImage image, int maxDimension)
    {
        if (Math.Max(image.Width, image.Height) > maxDimension)
            image.Resize(new MagickGeometry((uint)maxDimension, (uint)maxDimension));
    }

    private static BitmapSource ToBitmap(MagickImage image, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (image.GetColorProfile() is not null)
            image.TransformColorSpace(ColorProfiles.SRGB);
        else
            image.ColorSpace = ColorSpace.sRGB;
        var width = checked((int)image.Width);
        var height = checked((int)image.Height);
        var stride = checked(width * 4);
        using var pixels = image.GetPixels();
        var bytes = pixels.ToByteArray(PixelMapping.BGRA)
            ?? throw new InvalidOperationException("Декодер не вернул пиксели изображения.");
        token.ThrowIfCancellationRequested();
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bytes, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Save(string source, string destination, int quarterTurns, bool sameFile,
        MagickFormat format, CancellationToken token)
    {
        _ = NativeLimits.Value;
        token.ThrowIfCancellationRequested();
        if (sameFile)
        {
            using var frames = new MagickImageCollection();
            using var input = OpenRead(source);
            frames.Ping(input, new MagickReadSettings { FrameIndex = 0, FrameCount = 2 });
            if (frames.Count > 1 || frames.Any(frame => frame.GetAttribute("png:acTL") is not null))
                throw new InvalidOperationException("Файл содержит несколько кадров или слоёв. Сохраните первый кадр отдельной копией.");
        }

        var folder = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException("Папка для сохранения не найдена.");
        var temporary = Path.Combine(folder, $".photowoo-{Guid.NewGuid():N}.tmp");
        try
        {
            using var image = NewImage(token);
            using (var input = ExtraImageFormats.OpenImage(source))
            {
                if (Path.GetExtension(source).ToLowerInvariant() is ".ico" or ".icon" or ".icn" or ".cur" or ".ani" or ".jxr" or ".wdp" or ".hdp")
                {
                    var decoded = Load(source, int.MaxValue, true, token);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(decoded.Bitmap));
                    using var encoded = new MemoryStream();
                    encoder.Save(encoded);
                    encoded.Position = 0;
                    image.Read(encoded, MagickFormat.Png);
                }
                else
                {
                    var settings = CreateReadSettings(IsRaw(source));
                    ExtraImageFormats.SetReadFormat(source, settings);
                    ExtraImageFormats.Read(image, input, settings, source);
                }
            }
            token.ThrowIfCancellationRequested();
            image.AutoOrient();
            var turns = ((quarterTurns % 4) + 4) % 4;
            if (turns != 0)
                image.Rotate(turns * 90);
            image.ResetPage();
            image.Orientation = OrientationType.TopLeft;
            NormalizeMetadata(image);

            if (format == MagickFormat.Jpeg)
            {
                image.BackgroundColor = MagickColors.White;
                image.Alpha(AlphaOption.Remove);
                image.Quality = 95;
            }
            else if (format == MagickFormat.WebP)
            {
                image.Settings.SetDefine(MagickFormat.WebP, "lossless", true);
            }
            // Writing to a stream avoids inferring the format from the temporary extension.
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                image.Write(output, format);
                output.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            // The original is touched only after a complete, flushed output exists beside it.
            if (File.Exists(destination))
                File.Replace(temporary, destination, null);
            else
                File.Move(temporary, destination);
        }
        catch (MagickException) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
        finally
        {
            // Cancellation or encoder failure leaves the source intact and no half-written output.
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void NormalizeMetadata(MagickImage image)
    {
        var exif = image.GetExifProfile();
        if (exif is not null)
        {
            exif.SetValue(ExifTag.Orientation, (ushort)1);
            exif.SetValue(ExifTag.PixelXDimension, new Number(image.Width));
            exif.SetValue(ExifTag.PixelYDimension, new Number(image.Height));
            exif.RemoveThumbnail();
            image.SetProfile(exif);
        }
        image.RemoveProfile("dng:thumbnail");

        var xmp = image.GetProfile("xmp");
        if (xmp is null)
            return;
        try
        {
            using var stream = new MemoryStream(xmp.ToByteArray());
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            XNamespace tiff = "http://ns.adobe.com/tiff/1.0/";
            XNamespace exifNs = "http://ns.adobe.com/exif/1.0/";
            var updates = new Dictionary<XName, string>
            {
                [tiff + "Orientation"] = "1",
                [tiff + "ImageWidth"] = image.Width.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [tiff + "ImageLength"] = image.Height.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [exifNs + "PixelXDimension"] = image.Width.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [exifNs + "PixelYDimension"] = image.Height.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            foreach (var element in document.Descendants())
            {
                if (updates.TryGetValue(element.Name, out var value))
                    element.Value = value;
                foreach (var attribute in element.Attributes())
                    if (updates.TryGetValue(attribute.Name, out value))
                        attribute.Value = value;
            }
            image.SetProfile(new ImageProfile("xmp", Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting))));
        }
        catch (XmlException)
        {
            // Invalid XMP cannot be safely rewritten, and must not restore an obsolete orientation.
            image.RemoveProfile("xmp");
        }
    }

    private static MagickFormat GetOutputFormat(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".jpe" => MagickFormat.Jpeg,
        ".png" => MagickFormat.Png,
        ".tif" or ".tiff" => MagickFormat.Tiff,
        ".webp" => MagickFormat.WebP,
        _ => throw new NotSupportedException("Для сохранения выберите JPEG, PNG, TIFF или WebP.")
    };

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
}
