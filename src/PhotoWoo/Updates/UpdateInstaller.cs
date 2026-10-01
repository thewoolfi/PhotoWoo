using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhotoWoo.Updates;

public sealed record UpdateApplyResult(bool Success, string Code, string Message, string? Version = null,
    bool RolledBack = false, string? BackupDirectory = null, string? InstallationDirectory = null, string? StagingDirectory = null);

public sealed class UpdateInUseException(string message) : IOException(message);

/// <summary>Runs from the staged new executable, after the old viewer has exited.</summary>
public static class UpdateInstaller
{
    private const string NonceVariable = "PHOTOWOO_UPDATE_NONCE";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static string ResultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoWoo", "update-result.json");
    private static string UpdatesRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoWoo", "Updates");

    /// <summary>Keep this shared lease alive for the entire normal application process.</summary>
    public static FileStream AcquireApplicationLease(string installationDirectory) =>
        OpenLease(installationDirectory, FileShare.ReadWrite);

    public static Process Start(PreparedUpdate update, string installationDirectory, string? reopenPath = null, int? parentPid = null)
    {
        string target = ValidateInstallation(installationDirectory);
        string stage = ValidateStage(update.RootDirectory);
        string payload = UpdatePathGuard.AbsoluteDirectory(update.PayloadDirectory);
        ValidateStageRelationship(stage, payload, target);
        var newVersion = NormalizeVersion(update.Release.Version);
        ValidateVersion(payload, newVersion);
        RequireNewer(target, newVersion);
        if (!IsSha256(update.Sha256)) throw new InvalidDataException("Invalid package checksum.");
        var manifest = UpdateTransaction.CreateManifest(payload);
        ValidateArchive(stage, update.Sha256, manifest, newVersion);
        using var parent = Process.GetProcessById(parentPid ?? Environment.ProcessId);
        EnsureNoOtherViewer(target, parent.Id);
        // Detect read-only installations while the old viewer can still display an error.
        string probePath = Path.Combine(target, ".photowoo-probe-" + Guid.NewGuid().ToString("N"));
        using (var probe = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        foreach (var file in manifest)
        {
            string destination = UpdatePathGuard.ManifestPath(target, file.RelativePath);
            if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReadOnly) != 0)
                throw new UnauthorizedAccessException("An installed file is read-only.");
        }
        var plan = new UpdatePlan
        {
            RootDirectory = stage, PayloadDirectory = payload, InstallationDirectory = target,
            CurrentExecutable = Path.Combine(target, "PhotoWoo.exe"), NewVersion = newVersion.ToString(3),
            ParentProcessId = parent.Id, ParentStartTimeUtcTicks = parent.StartTime.ToUniversalTime().Ticks,
            Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), ArchiveSha256 = update.Sha256,
            ReopenPath = NormalizeReopenPath(reopenPath), Files = manifest
        };
        string planPath = Path.Combine(stage, "apply-plan.json");
        UpdatePathGuard.NoReparsePoints(planPath);
        using (var stream = new FileStream(planPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, plan, JsonOptions); stream.Flush(flushToDisk: true);
        }
        var start = new ProcessStartInfo(Path.Combine(payload, "PhotoWoo.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = payload
        };
        start.ArgumentList.Add("--photowoo-apply-update"); start.ArgumentList.Add(planPath);
        start.Environment[NonceVariable] = plan.Nonce;
        try { return Process.Start(start) ?? throw new IOException("The update helper could not start."); }
        catch
        {
            // This helper never started; make the same prepared package retryable without replacing another plan.
            try { UpdatePathGuard.NoReparsePoints(planPath); File.Delete(planPath); } catch { }
            throw;
        }
    }

    public static async Task<UpdateApplyResult> ApplyPlanAsync(string planPath, CancellationToken cancellationToken = default)
    {
        UpdatePlan? plan = null;
        bool parentExited = false;
        UpdateApplyResult Finish(UpdateApplyResult result) => SaveResult(result with
        { InstallationDirectory = plan?.InstallationDirectory, StagingDirectory = plan?.RootDirectory });
        try
        {
            plan = ReadAndValidatePlan(planPath);
            // A cancelled application close leaves the exact old process alive. Never kill it or modify its files.
            if (!await WaitForParentAsync(plan.ParentProcessId, plan.ParentStartTimeUtcTicks, TimeSpan.FromSeconds(60), cancellationToken))
                return Finish(new(false, "parent-running", "PhotoWoo did not close. No files were changed.", plan.NewVersion));
            parentExited = true;
            using var lease = OpenLease(plan.InstallationDirectory, FileShare.None);
            EnsureNoOtherViewer(plan.InstallationDirectory);
            ValidateInstallation(plan.InstallationDirectory);
            ValidateStageRelationship(plan.RootDirectory, plan.PayloadDirectory, plan.InstallationDirectory);
            var version = Version.Parse(plan.NewVersion);
            ValidateVersion(plan.PayloadDirectory, version);
            RequireNewer(plan.InstallationDirectory, version);
            ValidateArchive(plan.RootDirectory, plan.ArchiveSha256, plan.Files, version);

            UpdateTransactionOutcome outcome = await Task.Run(() => UpdateTransaction.Apply(
                plan.PayloadDirectory, plan.InstallationDirectory, plan.Files), cancellationToken);
            if (!outcome.Success)
            {
                var failure = Finish(new(false, outcome.RolledBack ? "apply-failed" : "rollback-failed",
                    outcome.Error ?? "The update could not be installed.", plan.NewVersion, outcome.RolledBack, outcome.BackupDirectory));
                lease.Dispose();
                if (outcome.RolledBack) TryLaunchViewer(plan);
                return failure;
            }
            var success = Finish(new(true, "updated", "PhotoWoo was updated successfully.", plan.NewVersion));
            lease.Dispose(); // The new viewer must be able to acquire its shared installation lease.
            try { LaunchViewer(plan); }
            catch (Exception launchError)
            {
                return Finish(new(true, "restart-failed", launchError.Message, plan.NewVersion));
            }
            return success;
        }
        catch (OperationCanceledException)
        {
            return Finish(new(false, "cancelled", "The update was cancelled before replacement.", plan?.NewVersion));
        }
        catch (Exception exception)
        {
            // Failure before the transaction changes no installed files. Other instances and locked files stay untouched.
            string code = exception is UpdateInUseException ? "in-use" : exception is InvalidDataException ? "invalid-package" : exception is UnauthorizedAccessException ? "access-denied" : "update-failed";
            var result = Finish(new(false, code, exception.Message, plan?.NewVersion));
            if (parentExited && plan is not null && code != "invalid-package")
            {
                // Restart only when no competing viewer or installer owns this installation.
                try
                {
                    using var lease = OpenLease(plan.InstallationDirectory, FileShare.None);
                    EnsureNoOtherViewer(plan.InstallationDirectory); ValidateInstallation(plan.InstallationDirectory);
                    lease.Dispose(); TryLaunchViewer(plan);
                }
                catch { }
            }
            return result;
        }
        finally { Environment.SetEnvironmentVariable(NonceVariable, null); }
    }

    public static UpdateApplyResult? ReadAndClearResult(string? installationDirectory = null)
    {
        try
        {
            UpdatePathGuard.NoReparsePoints(ResultPath);
            if (!File.Exists(ResultPath)) return null;
            using var stream = new FileStream(ResultPath, FileMode.Open, FileAccess.Read, FileShare.None);
            if (stream.Length > 128 * 1024) return null;
            var result = JsonSerializer.Deserialize<UpdateApplyResult>(stream);
            if (installationDirectory is not null && (result?.InstallationDirectory is null ||
                !Path.GetFullPath(result.InstallationDirectory).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(installationDirectory)), StringComparison.OrdinalIgnoreCase)))
                return null;
            stream.Dispose(); File.Delete(ResultPath); return result;
        }
        catch { return null; }
    }

    /// <summary>Call from the restarted viewer after a short delay, once the staging helper has exited.</summary>
    public static void CleanupCompletedStaging(UpdateApplyResult result)
    {
        if (!result.Success || result.StagingDirectory is null) return;
        try
        {
            string stage = ValidateStage(result.StagingDirectory);
            string running = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
            if (result.InstallationDirectory is null ||
                !Path.TrimEndingDirectorySeparator(Path.GetFullPath(result.InstallationDirectory)).Equals(running, StringComparison.OrdinalIgnoreCase)) return;
            if (UpdatePathGuard.IsWithin(running, stage) || running.Equals(stage, StringComparison.OrdinalIgnoreCase)) return;
            var directories = new List<string>(); var pending = new Stack<string>(); pending.Push(stage);
            while (pending.TryPop(out var directory))
            {
                UpdatePathGuard.NoReparsePoints(directory); directories.Add(directory);
                foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                {
                    UpdatePathGuard.NoReparsePoints(path);
                    if (Directory.Exists(path)) pending.Push(path);
                    else { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
                }
            }
            foreach (string directory in directories.AsEnumerable().Reverse())
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        catch (InvalidDataException) { }
        catch (ArgumentException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static UpdateApplyResult SaveResult(UpdateApplyResult result)
    {
        try
        {
            UpdatePathGuard.NoReparsePoints(ResultPath);
            Directory.CreateDirectory(Path.GetDirectoryName(ResultPath)!);
            string temporary = ResultPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(result, JsonOptions));
            File.Move(temporary, ResultPath, overwrite: true);
        }
        catch { /* Failure to write a receipt must never turn a committed update into a rollback. */ }
        return result;
    }

    internal static FileStream OpenLease(string installationDirectory, FileShare share)
    {
        // Merely viewing a portable installation does not require write access to it.
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installationDirectory));
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.ToUpperInvariant())));
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoWoo", "UpdateLocks");
        UpdatePathGuard.NoReparsePoints(directory); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, key + ".lock"); UpdatePathGuard.NoReparsePoints(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, share);
    }

    internal static async Task<bool> WaitForParentAsync(int processId, long startTimeTicks, TimeSpan timeout, CancellationToken token = default)
    {
        Process process;
        try { process = Process.GetProcessById(processId); }
        catch (ArgumentException) { return true; }
        using (process)
        {
            if (process.StartTime.ToUniversalTime().Ticks != startTimeTicks) return true; // PID was reused.
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
            bounded.CancelAfter(timeout);
            try { await process.WaitForExitAsync(bounded.Token); return true; }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
        }
    }

    private static UpdatePlan ReadAndValidatePlan(string planPath)
    {
        if (!Path.IsPathFullyQualified(planPath)) throw new InvalidDataException("Invalid update plan path.");
        planPath = Path.GetFullPath(planPath); UpdatePathGuard.NoReparsePoints(planPath);
        string stage = ValidateStage(Path.GetDirectoryName(planPath)!);
        if (!Path.GetFileName(planPath).Equals("apply-plan.json", StringComparison.Ordinal)) throw new InvalidDataException("Invalid update plan name.");
        using var stream = new FileStream(planPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 8 * 1024 * 1024) throw new InvalidDataException("The update plan is too large.");
        var plan = JsonSerializer.Deserialize<UpdatePlan>(stream) ?? throw new InvalidDataException("The update plan is empty.");
        string? expectedNonce = Environment.GetEnvironmentVariable(NonceVariable);
        if (plan.SchemaVersion != 1 || !IsSha256(plan.Nonce) || !IsSha256(expectedNonce) ||
            !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(plan.Nonce), Convert.FromHexString(expectedNonce!)))
            throw new InvalidDataException("The update helper was not started by PhotoWoo.");
        if (!stage.Equals(UpdatePathGuard.AbsoluteDirectory(plan.RootDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update plan belongs to a different staging directory.");
        plan.InstallationDirectory = ValidateInstallation(plan.InstallationDirectory);
        plan.PayloadDirectory = UpdatePathGuard.AbsoluteDirectory(plan.PayloadDirectory);
        ValidateStageRelationship(stage, plan.PayloadDirectory, plan.InstallationDirectory);
        if (!plan.PayloadDirectory.Equals(UpdatePathGuard.AbsoluteDirectory(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update helper must run from the prepared payload.");
        if (!Path.GetFullPath(plan.CurrentExecutable).Equals(Path.Combine(plan.InstallationDirectory, "PhotoWoo.exe"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid installed executable path.");
        if (!Version.TryParse(plan.NewVersion, out var version) || version.Build < 0 || version.Revision > 0 ||
            plan.ParentProcessId <= 0 || plan.ParentProcessId == Environment.ProcessId || plan.ParentStartTimeUtcTicks <= 0 ||
            !IsSha256(plan.ArchiveSha256) || plan.Files is null || plan.Files.Count is < 1 or > 5000)
            throw new InvalidDataException("The update plan contains invalid values.");
        plan.ReopenPath = NormalizeReopenPath(plan.ReopenPath);
        foreach (var file in plan.Files)
            if (file.Length < 0 || !IsSha256(file.Sha256)) throw new InvalidDataException("Invalid update manifest.");
        var actual = UpdateTransaction.CreateManifest(plan.PayloadDirectory);
        if (!actual.SequenceEqual(plan.Files)) throw new InvalidDataException("The prepared payload no longer matches its manifest.");
        return plan;
    }

    internal static string ValidateInstallation(string directory)
    {
        string target = UpdatePathGuard.AbsoluteDirectory(directory);
        if (!Directory.Exists(target) || string.Equals(target, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(target)!), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The installation directory is unavailable.");
        foreach (var folder in new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            string special = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(special) && target.Equals(Path.TrimEndingDirectorySeparator(special), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("PhotoWoo must be installed in its own folder before updating.");
        }
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows) && UpdatePathGuard.IsWithin(target, windows)) throw new InvalidDataException("A Windows system directory cannot be updated.");
        foreach (string name in new[] { "PhotoWoo.exe", "PhotoWoo.dll", "PhotoWoo.runtimeconfig.json", "PhotoWoo.deps.json" })
        {
            string path = Path.Combine(target, name); UpdatePathGuard.NoReparsePoints(path);
            if (!File.Exists(path)) throw new InvalidDataException("This directory does not contain a complete PhotoWoo installation.");
        }
        var assembly = AssemblyName.GetAssemblyName(Path.Combine(target, "PhotoWoo.dll"));
        if (assembly.Name != "PhotoWoo") throw new InvalidDataException("Invalid PhotoWoo assembly.");
        return target;
    }

    private static string ValidateStage(string directory)
    {
        string stage = UpdatePathGuard.AbsoluteDirectory(directory);
        string parent = Path.GetDirectoryName(stage) ?? "";
        if (!parent.Equals(UpdatePathGuard.AbsoluteDirectory(UpdatesRoot), StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(stage), "N", out _) || !Directory.Exists(stage))
            throw new InvalidDataException("Invalid update staging directory.");
        return stage;
    }

    private static void ValidateStageRelationship(string stage, string payload, string target)
    {
        if (!payload.Equals(Path.Combine(stage, "payload"), StringComparison.OrdinalIgnoreCase) ||
            UpdatePathGuard.IsWithin(stage, target) || UpdatePathGuard.IsWithin(target, stage) || stage.Equals(target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update staging and installation directories must be separate.");
        UpdatePathGuard.NoReparsePoints(payload); UpdatePathGuard.NoReparsePoints(target);
    }

    private static void ValidateVersion(string payload, Version version)
    {
        ValidateInstallation(payload);
        var actual = AssemblyName.GetAssemblyName(Path.Combine(payload, "PhotoWoo.dll")).Version;
        if (actual is null || NormalizeVersion(actual) != NormalizeVersion(version)) throw new InvalidDataException("The prepared update version does not match the release.");
        using var exe = File.OpenRead(Path.Combine(payload, "PhotoWoo.exe"));
        if (exe.ReadByte() != 'M' || exe.ReadByte() != 'Z') throw new InvalidDataException("Invalid update executable.");
    }

    internal static void RequireNewer(string target, Version version)
    {
        Version current = AssemblyName.GetAssemblyName(Path.Combine(target, "PhotoWoo.dll")).Version ?? new(0, 0, 0);
        if (NormalizeVersion(version) <= NormalizeVersion(current)) throw new InvalidDataException("The update must be newer than the installed version.");
    }

    internal static void ValidateArchive(string stage, string checksum, IReadOnlyList<UpdateFile> manifest, Version version)
    {
        string archive = Path.Combine(stage, "package.zip"); UpdatePathGuard.NoReparsePoints(archive);
        using var stream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(checksum, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded package checksum changed.");
        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var remaining = manifest.ToDictionary(file => file.RelativePath.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
        string prefix = "PhotoWoo-" + NormalizeVersion(version).ToString(3) + "/";
        bool wrapped = zip.Entries.Any(entry => entry.FullName.StartsWith(prefix, StringComparison.Ordinal));
        byte[] buffer = new byte[64 * 1024];
        foreach (var entry in zip.Entries)
        {
            string name = entry.FullName;
            if (wrapped)
            {
                if (!name.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("The package directory layout changed.");
                name = name[prefix.Length..];
            }
            if (name.Length == 0 || name.EndsWith('/')) continue;
            if (!remaining.Remove(name, out var file) || entry.Length != file.Length)
                throw new InvalidDataException("The prepared payload does not match the verified package.");
            using var input = entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0; int read;
            while ((read = input.Read(buffer)) != 0)
            {
                length += read;
                if (length > file.Length) throw new InvalidDataException("A package entry exceeded its declared size.");
                hash.AppendData(buffer, 0, read);
            }
            if (length != file.Length || !Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A prepared file differs from the verified package.");
        }
        if (remaining.Count != 0) throw new InvalidDataException("The prepared payload contains files absent from the verified package.");
    }

    internal static void EnsureNoOtherViewer(string target, int? parentToIgnore = null)
    {
        foreach (var process in Process.GetProcessesByName("PhotoWoo"))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId || process.Id == parentToIgnore) continue;
                string? executable;
                try { executable = process.MainModule?.FileName; }
                catch (System.ComponentModel.Win32Exception) { continue; } // File preflight handles inaccessible other-user instances.
                catch (InvalidOperationException) { continue; }
                if (executable is not null && Path.GetDirectoryName(executable)!.Equals(target, StringComparison.OrdinalIgnoreCase))
                    throw new UpdateInUseException("Another PhotoWoo window is using this installation. Close it before updating.");
            }
        }
    }

    private static void LaunchViewer(UpdatePlan plan)
    {
        var start = new ProcessStartInfo(plan.CurrentExecutable) { UseShellExecute = false, WorkingDirectory = plan.InstallationDirectory };
        start.Environment.Remove(NonceVariable);
        if (plan.ReopenPath is not null && File.Exists(plan.ReopenPath)) start.ArgumentList.Add(plan.ReopenPath);
        using var process = Process.Start(start) ?? throw new IOException("The updated PhotoWoo could not start.");
    }
    private static void TryLaunchViewer(UpdatePlan plan) { try { LaunchViewer(plan); } catch { } }
    private static Version NormalizeVersion(Version value) => new(value.Major, value.Minor, Math.Max(value.Build, 0));
    private static string? NormalizeReopenPath(string? path) => string.IsNullOrWhiteSpace(path) ? null :
        Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : throw new InvalidDataException("Invalid image path.");
    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private sealed class UpdatePlan
    {
        public int SchemaVersion { get; set; } = 1;
        public string RootDirectory { get; set; } = "";
        public string PayloadDirectory { get; set; } = "";
        public string InstallationDirectory { get; set; } = "";
        public string CurrentExecutable { get; set; } = "";
        public string NewVersion { get; set; } = "";
        public int ParentProcessId { get; set; }
        public long ParentStartTimeUtcTicks { get; set; }
        public string Nonce { get; set; } = "";
        public string ArchiveSha256 { get; set; } = "";
        public string? ReopenPath { get; set; }
        public List<UpdateFile> Files { get; set; } = [];
    }
}
