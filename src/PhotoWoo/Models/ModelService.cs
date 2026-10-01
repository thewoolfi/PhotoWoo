using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using SharpGLTF.Runtime;
using SharpGLTF.Schema2;
using SharpGLTF.Transforms;
using GltfMaterial = SharpGLTF.Schema2.Material;
using WpfMaterial = System.Windows.Media.Media3D.Material;

namespace PhotoWoo.Models;

public sealed record LoadedModel(Model3DGroup Scene, int Meshes, int Triangles, int Animations, TimeSpan LoadTime);

/// <summary>Detached, frozen WPF geometry. glTF is evaluated in its default pose, including skins and morphs.</summary>
public sealed class ModelService
{
    private static readonly SemaphoreSlim LoadSlot = new(1, 1);
    private const int MaximumVertices = 2_000_000;
    private const int MaximumTriangles = 2_000_000;

    public async Task<LoadedModel> LoadAsync(string path, CancellationToken token)
    {
        await LoadSlot.WaitAsync(token).ConfigureAwait(false);
        try { return await Task.Run(() => Load(path, token), token).ConfigureAwait(false); }
        finally { LoadSlot.Release(); }
    }

    private static LoadedModel Load(string path, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        Imaging.ImageService.EnsureNativeLimits();
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 256L * 1024 * 1024) throw new NotSupportedException("The model file exceeds the 256 MB loading limit.");
        long totalBytes = input.Length;
        var context = ReadContext.Create(name =>
        {
            token.ThrowIfCancellationRequested();
            var asset = ResolveAsset(directory, name);
            var length = new FileInfo(asset).Length;
            if (length > 256L * 1024 * 1024 || (totalBytes += length) > 512L * 1024 * 1024)
                throw new NotSupportedException("The model and its resources exceed the 512 MB loading limit.");
            var bytes = File.ReadAllBytes(asset);
            token.ThrowIfCancellationRequested();
            return new ArraySegment<byte>(bytes);
        });
        // The user-selected filename is literal, while satellite names are glTF URIs.
        var root = context.ReadSchema2(input);
        token.ThrowIfCancellationRequested();
        var scene = root.DefaultScene ?? root.LogicalScenes.FirstOrDefault()
            ?? throw new InvalidDataException("The file does not contain a 3D scene.");
        long vertexCount = root.LogicalMeshes.Sum(mesh => mesh.Primitives.Sum(p => (long)(p.GetVertexAccessor("POSITION")?.Count ?? 0)));
        if (vertexCount > MaximumVertices) throw new NotSupportedException("This model exceeds the 2 million vertex viewing limit.");
        var decoders = root.LogicalMeshes.Decode();
        var instance = SceneTemplate.Create(scene).CreateInstance();
        instance.Armature.SetPoseTransforms();
        var output = new Model3DGroup();
        var materials = new Dictionary<GltfMaterial, WpfMaterial>();
        long textureBytes = 0;
        var triangles = 0;
        var vertices = 0;
        foreach (var drawable in instance)
        foreach (var transform in InstancingTransform.Evaluate(drawable.Transform))
        {
            token.ThrowIfCancellationRequested();
            if (!transform.Visible) continue;
            foreach (var primitive in decoders[drawable.Template.LogicalMeshIndex].Primitives)
            {
                token.ThrowIfCancellationRequested();
                vertices = checked(vertices + primitive.VertexCount);
                if (vertices > MaximumVertices) throw new NotSupportedException("This scene exceeds the 2 million vertex viewing limit.");
                var geometry = new MeshGeometry3D
                {
                    Positions = new Point3DCollection(primitive.VertexCount),
                    Normals = new Vector3DCollection(primitive.VertexCount),
                    TextureCoordinates = new PointCollection(primitive.VertexCount)
                };
                var channel = primitive.Material?.FindChannel("BaseColor") ?? primitive.Material?.FindChannel("Diffuse");
                var uvSet = channel?.TextureTransform?.TextureCoordinateOverride ?? channel?.TextureCoordinate ?? 0;
                for (var i = 0; i < primitive.VertexCount; i++)
                {
                    if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
                    var p = primitive.GetPosition(i, transform);
                    var n = primitive.GetNormal(i, transform);
                    if (!Finite(p) || !Finite(n)) throw new InvalidDataException("The model contains invalid coordinates.");
                    geometry.Positions.Add(new Point3D(p.X, p.Y, p.Z));
                    geometry.Normals.Add(new Vector3D(n.X, n.Y, n.Z));
                    var uv = uvSet < primitive.TexCoordsCount ? primitive.GetTextureCoord(i, uvSet) : Vector2.Zero;
                    if (channel?.TextureTransform is { } uvTransform) uv = Vector2.Transform(uv, uvTransform.Matrix);
                    geometry.TextureCoordinates.Add(new Point(uv.X, uv.Y));
                }
                foreach (var (a, b, c) in primitive.TriangleIndices)
                {
                    if ((triangles & 1023) == 0) token.ThrowIfCancellationRequested();
                    if (++triangles > MaximumTriangles) throw new NotSupportedException("This scene exceeds the 2 million triangle viewing limit.");
                    geometry.TriangleIndices.Add(a);
                    geometry.TriangleIndices.Add(transform.FlipFaces ? c : b);
                    geometry.TriangleIndices.Add(transform.FlipFaces ? b : c);
                }
                if (geometry.TriangleIndices.Count == 0) continue;
                geometry.Freeze();
                WpfMaterial material;
                if (primitive.Material is not { } source) material = DefaultMaterial();
                else if (!materials.TryGetValue(source, out material!))
                    materials[source] = material = CreateMaterial(source, token, ref textureBytes);
                var model = new GeometryModel3D(geometry, material);
                if (primitive.Material?.DoubleSided == true) model.BackMaterial = material;
                model.Freeze();
                output.Children.Add(model);
            }
        }
        if (output.Children.Count == 0) throw new NotSupportedException("The model has no triangle surfaces to display.");
        output.Freeze();
        return new LoadedModel(output, output.Children.Count, triangles, root.LogicalAnimations.Count, timer.Elapsed);
    }

    internal static string ResolveAsset(string directory, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) || name.Contains(':'))
            throw new InvalidDataException("Only local, relative model resources are supported.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, name));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Model resources must be inside the model's folder.");
        var current = Path.TrimEndingDirectorySeparator(root);
        foreach (var part in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked model resources are not supported.");
        }
        return path;
    }

    private static WpfMaterial CreateMaterial(GltfMaterial source, CancellationToken token, ref long textureBytes)
    {
        var channel = source.FindChannel("BaseColor") ?? source.FindChannel("Diffuse");
        var factor = channel?.Color ?? Vector4.One;
        Brush brush = new SolidColorBrush(Color.FromScRgb(source.Alpha == AlphaMode.OPAQUE ? 1 : factor.W, factor.X, factor.Y, factor.Z));
        if (channel?.Texture is { } texture)
        {
            var image = texture.PrimaryImage;
            if (image.Content.IsKtx2 && texture.FallbackImage is { } fallback) image = fallback;
            if (image.Content.IsKtx2) throw new NotSupportedException("KTX2 textures are not supported. Export PNG or JPEG textures.");
            token.ThrowIfCancellationRequested();
            using var stream = image.Content.Open();
            using var decoded = new ImageMagick.MagickImage(stream);
            var maxSize = Math.Max(decoded.Width, decoded.Height);
            if (maxSize > 2048) decoded.Resize(new ImageMagick.MagickGeometry(2048, 2048));
            textureBytes += (long)decoded.Width * decoded.Height * 4;
            if (textureBytes > 128L * 1024 * 1024)
                throw new NotSupportedException("The model textures exceed the 128 MB viewing limit.");
            using var encoded = new MemoryStream();
            decoded.Write(encoded, ImageMagick.MagickFormat.Png);
            encoded.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = encoded;
            bitmap.EndInit(); bitmap.Freeze();
            var pixels = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            var stride = checked(pixels.PixelWidth * 4);
            var bytes = new byte[checked(stride * pixels.PixelHeight)];
            pixels.CopyPixels(bytes, stride, 0);
            for (var i = 0; i < bytes.Length; i += 4)
            {
                if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
                bytes[i] = (byte)Math.Clamp(bytes[i] * factor.Z, 0, 255);
                bytes[i + 1] = (byte)Math.Clamp(bytes[i + 1] * factor.Y, 0, 255);
                bytes[i + 2] = (byte)Math.Clamp(bytes[i + 2] * factor.X, 0, 255);
                var alpha = bytes[i + 3] / 255f * factor.W;
                bytes[i + 3] = source.Alpha switch
                {
                    AlphaMode.OPAQUE => 255,
                    AlphaMode.MASK => alpha >= source.AlphaCutoff ? (byte)255 : (byte)0,
                    _ => (byte)Math.Clamp(alpha * 255, 0, 255)
                };
            }
            var tinted = BitmapSource.Create(pixels.PixelWidth, pixels.PixelHeight, 96, 96, PixelFormats.Bgra32, null, bytes, stride);
            tinted.Freeze();
            brush = new ImageBrush(tinted) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1), TileMode = TileMode.Tile };
        }
        brush.Freeze();
        WpfMaterial material = source.Unlit ? new EmissiveMaterial(brush) : new DiffuseMaterial(brush);
        material.Freeze();
        return material;
    }

    private static WpfMaterial DefaultMaterial()
    {
        var material = new DiffuseMaterial(Brushes.White);
        material.Freeze();
        return material;
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
