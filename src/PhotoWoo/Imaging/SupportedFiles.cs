using System.IO;

namespace PhotoWoo.Imaging;

public static class SupportedFiles
{
    public static bool IsModel(string path) => Path.GetExtension(path).ToLowerInvariant() is ".glb" or ".gltf";
    public static bool IsSupported(string path) => IsModel(path) || ImageService.IsSupported(path);
    public static IReadOnlyList<string> Extensions { get; } = Array.AsReadOnly(
        ImageService.SupportedExtensions.Concat([".glb", ".gltf"]).Order(StringComparer.OrdinalIgnoreCase).ToArray());
    public static string Filter(string all, string images, string models, string other) =>
        $"{all}|{Patterns(Extensions)}|{images}|{Patterns(ImageService.SupportedExtensions)}|{models}|*.glb;*.gltf|{other}|*.*";
    private static string Patterns(IEnumerable<string> extensions) => string.Join(';', extensions.Select(extension => "*" + extension));
}
