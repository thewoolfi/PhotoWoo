using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ImageMagick;

namespace PhotoWoo.Imaging;

internal static class ExtraImageFormats
{
    private const int MaximumDocumentBytes = 64 * 1024 * 1024;

    public static Stream OpenImage(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".icns")
        {
            using var file = File.OpenRead(path);
            using var input = CopyBounded(file, MaximumDocumentBytes);
            var bytes = input.GetBuffer();
            if (input.Length < 8 || Encoding.ASCII.GetString(bytes, 0, 4) != "icns")
                throw new InvalidDataException("Invalid ICNS header.");
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4));
            if (length > input.Length || length < 8) throw new InvalidDataException("Invalid ICNS length.");
            var selected = -1; var selectedLength = 0; long largest = 0;
            for (var offset = 8; offset + 8 <= length;)
            {
                var chunkLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 4)));
                if (chunkLength < 8 || (long)offset + chunkLength > length) throw new InvalidDataException("Invalid ICNS chunk.");
                var data = offset + 8;
                if (chunkLength >= 32 && bytes.AsSpan(data, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                {
                    var area = (long)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(data + 16)) *
                        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(data + 20));
                    if (area > largest) { largest = area; selected = data; selectedLength = chunkLength - 8; }
                }
                offset += chunkLength;
            }
            if (selected < 0) throw new NotSupportedException("This legacy ICNS has no PNG representation. Only PNG-based ICNS icons are supported.");
            return new MemoryStream(bytes, selected, selectedLength, writable: false);
        }
        if (extension is ".ora" or ".kra")
        {
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.GetEntry("mergedimage.png")
                ?? throw new InvalidDataException("The document does not contain a merged image (mergedimage.png).");
            if (entry.Length > 256L * 1024 * 1024) throw new InvalidDataException("The merged image is too large.");
            using var source = entry.Open();
            return CopyBounded(source, 256 * 1024 * 1024);
        }
        if (extension is ".svg" or ".svgz")
        {
            using var file = File.OpenRead(path);
            using var source = extension == ".svgz" ? new GZipStream(file, CompressionMode.Decompress) : (Stream)file;
            using var limited = CopyBounded(source, MaximumDocumentBytes);
            using var reader = XmlReader.Create(limited, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore, XmlResolver = null,
                MaxCharactersInDocument = MaximumDocumentBytes
            });
            var document = XDocument.Load(reader);
            if (document.Root?.Name.LocalName != "svg") throw new InvalidDataException("Invalid SVG document.");
            // SVG is a local picture: do not allow it to load other files or network resources.
            foreach (var element in document.Descendants())
            {
                if (element.Name.LocalName is "script" or "foreignObject")
                    throw new NotSupportedException("SVG scripts and embedded HTML are not supported.");
                foreach (var attribute in element.Attributes())
                {
                    if (attribute.Name.LocalName is "href" or "src")
                    {
                        var value = attribute.Value.Trim();
                        if (value.Length != 0 && !value.StartsWith('#') &&
                            !Regex.IsMatch(value, @"^data:image/(png|jpeg|jpg|webp);base64,", RegexOptions.IgnoreCase))
                            throw new NotSupportedException("SVG external resources are not supported. Embed the image in the SVG.");
                    }
                }
            }
            var xml = document.ToString();
            if (Regex.IsMatch(xml, @"@import|url\s*\(\s*['""]?\s*[^#\s'""]", RegexOptions.IgnoreCase))
                throw new NotSupportedException("SVG external styles and resources are not supported.");
            return new MemoryStream(Encoding.UTF8.GetBytes(xml), writable: false);
        }
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }

    public static Stream OpenIcon(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".cur" or ".ani"))
            return File.OpenRead(path);
        using var source = File.OpenRead(path);
        var memory = CopyBounded(source, MaximumDocumentBytes);
        if (extension == ".ani")
        {
            var content = memory.ToArray(); memory.Dispose();
            if (content.Length < 12 || Encoding.ASCII.GetString(content, 0, 4) != "RIFF" ||
                Encoding.ASCII.GetString(content, 8, 4) != "ACON")
                throw new InvalidDataException("Invalid ANI header.");
            var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(content.AsSpan(4)) + 8);
            if (length > content.Length || length < 12) throw new InvalidDataException("Invalid ANI length.");
            var icon = FindAniIcon(content, 12, length, 0);
            if (icon is null) throw new InvalidDataException("The ANI contains no icon frames.");
            memory = new MemoryStream(); memory.Write(icon); memory.Position = 0;
        }
        var bytes = memory.GetBuffer();
        if (extension == ".ani" && memory.Length >= 6 && BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2)) == 1)
            return memory;
        if (memory.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2)) != 2)
            throw new InvalidDataException("Invalid cursor header.");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
        if (6L + 16L * count > memory.Length) throw new InvalidDataException("Invalid cursor directory.");
        // CUR uses the ICO layout; replace hotspot fields with icon plane/depth fields.
        bytes[2] = 1;
        for (var i = 0; i < count; i++)
        {
            var offset = 6 + i * 16;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 4), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 6), 32);
        }
        return memory;
    }

    private static byte[]? FindAniIcon(byte[] bytes, int start, int end, int depth)
    {
        if (depth > 8) throw new InvalidDataException("Invalid ANI nesting.");
        for (var offset = start; offset + 8 <= end;)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4)));
            var data = offset + 8;
            if ((long)data + length > end) throw new InvalidDataException("Invalid ANI frame.");
            var kind = Encoding.ASCII.GetString(bytes, offset, 4);
            if (kind == "icon") return bytes.AsSpan(data, length).ToArray();
            if (kind == "LIST" && length >= 4 && FindAniIcon(bytes, data + 4, data + length, depth + 1) is { } icon) return icon;
            offset = checked(data + length + (length & 1));
        }
        return null;
    }

    public static void SetReadFormat(string path, MagickReadSettings settings)
    {
        // Headerless/ambiguous containers require an explicit coder when read through a stream.
        var format = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".svg" or ".svgz" => MagickFormat.Svg,
            ".ora" or ".kra" or ".icns" => MagickFormat.Png,
            ".tga" or ".targa" or ".icb" or ".vda" or ".vst" => MagickFormat.Tga,
            ".pct" or ".pict" => MagickFormat.Pict,
            ".wmf" => MagickFormat.Wmf,
            ".rgb" or ".rgba" or ".bw" or ".int" or ".inta" or ".sgi" => MagickFormat.Sgi,
            ".wbmp" => MagickFormat.Wbmp,
            ".cut" => MagickFormat.Cut,
            ".art" => MagickFormat.Art,
            ".sct" => MagickFormat.Sct,
            _ => MagickFormat.Unknown
        };
        if (format != MagickFormat.Unknown) settings.Format = format;
    }

    public static void Read(MagickImage image, Stream stream, MagickReadSettings settings, string path)
    {
        try { image.Read(stream, settings); }
        catch (MagickMissingDelegateErrorException) when (Path.GetExtension(path).Equals(".pic", StringComparison.OrdinalIgnoreCase))
        {
            // PIC is also used by Radiance: try its signature first, then the headerless PICT alias.
            stream.Position = 0;
            settings.Format = MagickFormat.Pict;
            image.Read(stream, settings);
        }
    }

    private static MemoryStream CopyBounded(Stream source, int maximum)
    {
        var output = new MemoryStream();
        try
        {
            var buffer = new byte[81920];
            int count;
            while ((count = source.Read(buffer)) != 0)
            {
                if (output.Length + count > maximum) throw new InvalidDataException("The embedded document is too large.");
                output.Write(buffer, 0, count);
            }
            output.Position = 0;
            return output;
        }
        catch { output.Dispose(); throw; }
    }
}
