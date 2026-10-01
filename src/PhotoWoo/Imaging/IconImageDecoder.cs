using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoWoo.Imaging;

/// <summary>Reads the best embedded ICO frame, including PNG payloads and legacy AND masks.</summary>
internal static class IconImageDecoder
{
    public static (BitmapSource Bitmap, int Width, int Height) Decode(Stream stream, int maxDimension,
        bool fullResolution, CancellationToken token)
    {
        try
        {
            var entry = ReadEntries(stream, token).OrderByDescending(item => (long)item.Width * item.Height)
                .ThenByDescending(item => item.BitDepth).First();
            // WPF reorders ICO Frames by its own icon preference. Make a single-entry container
            // so the chosen original directory entry is decoded without an ambiguous index mapping.
            using var selectedStream = new MemoryStream(checked((int)entry.Size + 22));
            using (var writer = new BinaryWriter(selectedStream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1);
                writer.Write(entry.Directory.AsSpan(0, 12));
                writer.Write(22);
                stream.Position = entry.Offset;
                var payload = new byte[checked((int)entry.Size)];
                stream.ReadExactly(payload);
                writer.Write(payload);
            }
            token.ThrowIfCancellationRequested();
            selectedStream.Position = 0;
            var decoder = new IconBitmapDecoder(selectedStream, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.None);
            var selected = decoder.Frames[0];
            token.ThrowIfCancellationRequested();
            var width = selected.PixelWidth;
            var height = selected.PixelHeight;
            BitmapSource pixels = selected;
            if (!fullResolution && Math.Max(width, height) > maxDimension)
            {
                var scale = (double)maxDimension / Math.Max(width, height);
                pixels = new TransformedBitmap(pixels, new ScaleTransform(scale, scale));
            }
            if (pixels.Format != PixelFormats.Bgra32)
                pixels = new FormatConvertedBitmap(pixels, PixelFormats.Bgra32, null, 0);

            // A frozen decoder frame can still retain its decoder/stream. A pixel copy explicitly
            // detaches the returned image and preserves both straight alpha and legacy icon masks.
            var stride = checked(pixels.PixelWidth * 4);
            var buffer = new byte[checked(stride * pixels.PixelHeight)];
            pixels.CopyPixels(buffer, stride, 0);
            token.ThrowIfCancellationRequested();
            var bitmap = BitmapSource.Create(pixels.PixelWidth, pixels.PixelHeight, 96, 96,
                PixelFormats.Bgra32, null, buffer, stride);
            bitmap.Freeze();
            return (bitmap, width, height);
        }
        catch (Exception error) when (error is FileFormatException or COMException or NotSupportedException
            or ArgumentException or EndOfStreamException or OverflowException)
        {
            throw new FileFormatException("Не удалось открыть ICO: файл повреждён или не содержит читаемых значков.", error);
        }
    }

    private sealed record Entry(int Width, int Height, int BitDepth, uint Size, uint Offset, byte[] Directory);

    private static Entry[] ReadEntries(Stream stream, CancellationToken token)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (stream.Length < 6 || reader.ReadUInt16() != 0 || reader.ReadUInt16() != 1)
            throw new FileFormatException("Неверный заголовок ICO.");
        var count = reader.ReadUInt16();
        var directoryLength = 6 + count * 16;
        if (count == 0 || stream.Length < directoryLength)
            throw new FileFormatException("В ICO нет кадров или повреждён каталог кадров.");
        var entries = new Entry[count];
        for (var index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            stream.Position = 6 + index * 16;
            var directory = reader.ReadBytes(16);
            var width = directory[0] == 0 ? 256 : directory[0];
            var height = directory[1] == 0 ? 256 : directory[1];
            var depth = (int)BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(6));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(8));
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(12));
            if (size < 12 || offset < directoryLength || (long)offset + size > stream.Length)
                throw new FileFormatException("Кадр ICO выходит за границы файла.");
            stream.Position = offset;
            var header = reader.ReadBytes((int)Math.Min(size, 26));

            // WIC often exposes even a palette icon as BGRA32. Read the encoded depth to make
            // meaningful quality choices when several entries have the same dimensions.
            if (header.Length >= 26 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            {
                var channels = header[25] switch { 0 or 3 => 1, 2 => 3, 4 => 2, 6 => 4, _ => 0 };
                depth = header[24] * channels;
                width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16)));
                height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20)));
            }
            else
            {
                var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header);
                if (headerSize >= 40 && header.Length >= 16)
                {
                    depth = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14));
                    width = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
                    height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8))) / 2;
                }
                else if (headerSize == 12)
                {
                    depth = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10));
                    width = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
                    height = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6)) / 2;
                }
            }
            if (width <= 0 || height <= 0)
                throw new FileFormatException("Кадр ICO имеет неверные размеры.");
            entries[index] = new Entry(width, height, depth, size, offset, directory);
        }
        return entries;
    }
}
