using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace PhotoWoo.Updates;

internal sealed record UpdateFile(string RelativePath, long Length, string Sha256);
internal sealed record UpdateTransactionOutcome(bool Success, bool RolledBack, string? BackupDirectory, string? Error);

/// <summary>Copies only package files. Files outside the manifest are never removed or replaced.</summary>
internal static class UpdateTransaction
{
    internal static List<UpdateFile> CreateManifest(string payload)
    {
        var files = new List<UpdateFile>();
        var pending = new Stack<string>(); pending.Push(payload);
        while (pending.TryPop(out var directory))
        {
            UpdatePathGuard.NoReparsePoints(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                UpdatePathGuard.NoReparsePoints(entry);
                if (Directory.Exists(entry)) { pending.Push(entry); continue; }
                var relative = Path.GetRelativePath(payload, entry);
                UpdatePathGuard.ManifestPath(payload, relative);
                using var stream = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read);
                files.Add(new(relative, stream.Length, Convert.ToHexString(SHA256.HashData(stream))));
                if (files.Count > 5000) throw new InvalidDataException("The update contains too many files.");
            }
        }
        return files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static UpdateTransactionOutcome Apply(string payload, string target, IReadOnlyList<UpdateFile> files,
        Action<int>? afterReplacement = null)
    {
        if (files.Count == 0) throw new InvalidDataException("The update payload is empty.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var destinations = new List<(UpdateFile File, string Source, string Target, bool Existed)>();
        // Finish every lock/path check before creating the transaction or replacing any application file.
        foreach (var file in files)
        {
            if (!names.Add(file.RelativePath)) throw new InvalidDataException("Duplicate update path.");
            var source = UpdatePathGuard.ManifestPath(payload, file.RelativePath);
            var destination = UpdatePathGuard.ManifestPath(target, file.RelativePath);
            if (!File.Exists(source) || Directory.Exists(destination)) throw new IOException("An update file is unavailable.");
            bool existed = File.Exists(destination);
            if (existed)
            {
                using var probe = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            destinations.Add((file, source, destination, existed));
        }

        string backup = Path.Combine(target, ".photowoo-backup-" + Guid.NewGuid().ToString("N"));
        var committed = new List<(string Target, string Original, bool Existed)>();
        var createdDirectories = new List<string>();
        try
        {
            Directory.CreateDirectory(backup);
            File.SetAttributes(backup, File.GetAttributes(backup) | FileAttributes.Hidden);
            foreach (var entry in destinations)
            {
                string incoming = Path.Combine(backup, "incoming", entry.File.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(incoming)!);
                using (var input = new FileStream(entry.Source, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (input.Length != entry.File.Length ||
                        !Convert.ToHexString(SHA256.HashData(input)).Equals(entry.File.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("An update file changed after preparation.");
                    input.Position = 0;
                    using var output = new FileStream(incoming, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    input.CopyTo(output); output.Flush(flushToDisk: true);
                }
                if (entry.Existed)
                {
                    string original = Path.Combine(backup, "original", entry.File.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                    File.Copy(entry.Target, original, overwrite: false);
                }
            }
            // A complete journal and originals remain available if the process or machine stops unexpectedly.
            File.WriteAllText(Path.Combine(backup, "transaction.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, target, state = "prepared",
                files = destinations.Select(entry => new { path = entry.File.RelativePath, existed = entry.Existed })
            }));
            foreach (var entry in destinations)
            {
                UpdatePathGuard.NoReparsePoints(entry.Target);
                EnsureDirectories(Path.GetDirectoryName(entry.Target)!, target, createdDirectories);
                string incoming = Path.Combine(backup, "incoming", entry.File.RelativePath);
                string original = Path.Combine(backup, "original", entry.File.RelativePath);
                if (entry.Existed) File.Replace(incoming, entry.Target, null);
                else File.Move(incoming, entry.Target, overwrite: false);
                committed.Add((entry.Target, original, entry.Existed));
                afterReplacement?.Invoke(committed.Count);
            }
            CleanOwnedTransaction(backup, target);
            return new(true, false, null, null);
        }
        catch (Exception exception)
        {
            var failures = new List<string>();
            foreach (var entry in committed.AsEnumerable().Reverse())
            {
                try
                {
                    UpdatePathGuard.NoReparsePoints(entry.Target);
                    UpdatePathGuard.NoReparsePoints(entry.Original);
                    if (entry.Existed)
                    {
                        if (File.Exists(entry.Target)) File.Replace(entry.Original, entry.Target, null);
                        else File.Move(entry.Original, entry.Target, overwrite: false);
                    }
                    else File.Delete(entry.Target);
                }
                catch (Exception rollbackError) { failures.Add(rollbackError.Message); }
            }
            foreach (string directory in createdDirectories.AsEnumerable().Reverse())
            {
                try { if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            bool restored = failures.Count == 0;
            if (restored) CleanOwnedTransaction(backup, target);
            return new(false, restored, restored ? null : backup,
                exception.Message + (restored ? "" : " Rollback: " + string.Join("; ", failures)));
        }
    }

    private static void EnsureDirectories(string directory, string target, List<string> created)
    {
        if (Directory.Exists(directory)) { UpdatePathGuard.NoReparsePoints(directory); return; }
        if (!UpdatePathGuard.IsWithin(directory, target)) throw new IOException("Invalid destination directory.");
        EnsureDirectories(Path.GetDirectoryName(directory)!, target, created);
        Directory.CreateDirectory(directory); created.Add(directory);
    }

    private static void CleanOwnedTransaction(string directory, string target)
    {
        // Never recursively delete an installation path. Remove only this owned transaction's entries.
        try
        {
            if (!UpdatePathGuard.IsWithin(directory, target) ||
                !Path.GetFileName(directory).StartsWith(".photowoo-backup-", StringComparison.Ordinal)) return;
            var directories = new List<string>(); var pending = new Stack<string>(); pending.Push(directory);
            while (pending.TryPop(out var current))
            {
                UpdatePathGuard.NoReparsePoints(current); directories.Add(current);
                foreach (string entry in Directory.EnumerateFileSystemEntries(current))
                {
                    UpdatePathGuard.NoReparsePoints(entry);
                    if (Directory.Exists(entry)) pending.Push(entry); else File.Delete(entry);
                }
            }
            foreach (string child in directories.AsEnumerable().Reverse()) Directory.Delete(child);
        }
        catch (InvalidDataException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal static class UpdatePathGuard
{
    internal static string AbsoluteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new InvalidDataException("An absolute local path is required.");
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        NoReparsePoints(normalized);
        return normalized;
    }

    internal static bool IsWithin(string path, string parent) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static string ManifestPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') ||
            relative.Split('\\', '/').Any(part => part is "" or "." or ".." ||
                part.Equals(".git", StringComparison.OrdinalIgnoreCase) || part.StartsWith(".photowoo-", StringComparison.OrdinalIgnoreCase) ||
                part.EndsWith(' ') || part.EndsWith('.')))
            throw new InvalidDataException("Unsafe update file path.");
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsWithin(path, root)) throw new InvalidDataException("An update file leaves its directory.");
        NoReparsePoints(path);
        return path;
    }

    internal static void NoReparsePoints(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Links and reparse points are not supported for updates.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
