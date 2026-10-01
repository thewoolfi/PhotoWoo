using System.Windows.Media.Imaging;

namespace PhotoWoo.Imaging;

/// <summary>A detached, frozen display bitmap; dimensions describe the oriented source.</summary>
public sealed record LoadedImage(
    BitmapSource Bitmap,
    int SourceWidth,
    int SourceHeight,
    bool IsPreview,
    string Format,
    TimeSpan DecodeTime);
