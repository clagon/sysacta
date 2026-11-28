using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Apps;
using static Sysacta.Windows.Apps.NativeMethods;

namespace Sysacta.Windows.Apps;

/// <summary>
/// Win32 implementation of <see cref="IAppPlatform"/>. Executables are judged only through handles (final path, file
/// id, attributes, PE header, content hash); processes only through one handle each (creation time, token, protection).
/// No shell, ShellExecute, verb, URI, PATH search, environment expansion or privilege is used.
/// </summary>
public sealed class WindowsAppPlatform : IAppPlatform
{
    private const int MaxSnapshot = 4096;
    private readonly Lazy<bool> _elevated = new(DetectElevated);
    // PublicationOnly: a transient failure is retried on the next call instead of being cached.
    private readonly Lazy<SelfInfo> _self = new(DetectSelf, LazyThreadSafetyMode.PublicationOnly);
    private readonly Lazy<string[]> _protectedFolders = new(ProtectedFolders);

    public bool IsElevated => _elevated.Value;

    // ---- Executables ----

    public ExecutableCheck Inspect(string path, bool computeHash, CancellationToken cancellationToken)
    {
        // A hash is taken only while writers are excluded, so it describes one coherent file content.
        using var opened = OpenExecutable(path, exclusive: computeHash, out var check);
        if (opened is null || !computeHash)
        {
            return check;
        }

        return check with { Identity = check.Identity! with { Sha256 = Hash(opened.File, check.Identity.Size, cancellationToken) } };
    }

    public IPreparedLaunch PrepareLaunch(RegisteredApp app, CancellationToken cancellationToken)
    {
        var opened = OpenExecutable(app.Path, exclusive: true, out var check);
        try
        {
            if (opened is null)
            {
                throw check.Refusal switch
                {
                    ExecutableRefusal.NotFound or ExecutableRefusal.PathMismatch or ExecutableRefusal.ReparsePoint or ExecutableRefusal.IdentityChanged
                        => Changed(),
                    ExecutableRefusal.Unreadable => new ToolFailureException(ErrorCodes.OsApiError, "The executable could not be opened for verification (it may be in use or being updated). Nothing was launched.", retryable: true),
                    _ => new ToolFailureException(ErrorCodes.NotSupported, "The executable no longer passes Sysacta's safety rules. Nothing was launched."),
                };
            }

            var identity = check.Identity!;
            if (!identity.SameFile(app.Identity) || identity.Size != app.Identity.Size
                || !string.Equals(check.CanonicalPath, app.Path, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Hash(opened.File, identity.Size, cancellationToken), app.Identity.Sha256, StringComparison.Ordinal))
            {
                throw Changed();
            }

            // Everything that can fail without side effects is done here, before the start point.
            var expectedImage = NtPath(opened.File) ?? throw new ToolFailureException(ErrorCodes.OsApiError, "The executable could not be identified. Nothing was launched.", retryable: true);
            return new PreparedLaunch(opened, app.Path, expectedImage, (ExecutablePolicy.BuildCommandLine(app.Path, app.FixedArgs) + "\0").ToCharArray());
        }
        catch
        {
            opened?.Dispose();
            throw;
        }

        static ToolFailureException Changed() =>
            new(ErrorCodes.TargetChanged, "The executable is not the one the local user approved (moved, replaced or updated). It must be re-approved in Sysacta. Nothing was launched.");
    }

    /// <summary>
    /// Opens every parent folder and the file without following reparse points and applies all rules. With
    /// <paramref name="exclusive"/>, the folders are held without delete sharing (cannot be renamed or replaced) and the
    /// file without write/delete sharing (cannot be modified, renamed or deleted) until the result is disposed. Folders
    /// are opened with list access so the share mode is enforced (an attribute-only open records no sharing).
    /// </summary>
    private OpenedExecutable? OpenExecutable(string path, bool exclusive, out ExecutableCheck check)
    {
        var refusal = ExecutablePolicy.CheckPath(path);
        if (refusal != ExecutableRefusal.None)
        {
            check = ExecutableCheck.Refused(refusal);
            return null;
        }

        if (GetDriveTypeW(path[..3]) != DriveFixed)
        {
            check = ExecutableCheck.Refused(ExecutableRefusal.NotLocalFixedDrive);
            return null;
        }

        var opened = new OpenedExecutable();
        try
        {
            check = Open(opened, path, exclusive);
            if (check.IsUsable)
            {
                return opened;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            check = ExecutableCheck.Refused(ExecutableRefusal.Unreadable);
        }
        catch
        {
            opened.Dispose();
            throw;
        }

        opened.Dispose();
        return null;
    }

    private ExecutableCheck Open(OpenedExecutable opened, string path, bool exclusive)
    {
        var share = FileShareRead | FileShareWrite | (exclusive ? 0 : FileShareDelete);
        for (var end = path.IndexOf('\\', 3); end > 0; end = path.IndexOf('\\', end + 1))
        {
            var folder = CreateFileW(path[..end], FileListDirectory | FileReadAttributes | Synchronize, share, IntPtr.Zero, OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
            if (folder.IsInvalid)
            {
                return ExecutableCheck.Refused(OpenFailure(Marshal.GetLastWin32Error()));
            }

            opened.Folders.Add(folder);
            if (!GetFileInformationByHandle(folder, out var folderInfo))
            {
                return ExecutableCheck.Refused(ExecutableRefusal.Unreadable);
            }

            if ((folderInfo.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                return ExecutableCheck.Refused(ExecutableRefusal.ReparsePoint);
            }

            if ((folderInfo.FileAttributes & FileAttributeDirectory) == 0)
            {
                return ExecutableCheck.Refused(ExecutableRefusal.InvalidPath);
            }
        }

        opened.File = CreateFileW(path, GenericRead, exclusive ? FileShareRead : FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting,
            FileFlagOpenReparsePoint | FileFlagSequentialScan, IntPtr.Zero);
        if (opened.File.IsInvalid)
        {
            return ExecutableCheck.Refused(OpenFailure(Marshal.GetLastWin32Error()));
        }

        if (!GetFileInformationByHandle(opened.File, out var info) || !GetFileInformationByHandleEx(opened.File, FileIdInfoClass, out var id, (uint)Marshal.SizeOf<FileIdInfo>()))
        {
            return ExecutableCheck.Refused(ExecutableRefusal.Unreadable);
        }

        if ((info.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            return ExecutableCheck.Refused(ExecutableRefusal.ReparsePoint);
        }

        if ((info.FileAttributes & FileAttributeDirectory) != 0)
        {
            return ExecutableCheck.Refused(ExecutableRefusal.NotExe);
        }

        var finalPath = FinalPath(opened.File);
        if (finalPath is null || !string.Equals(finalPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return ExecutableCheck.Refused(ExecutableRefusal.PathMismatch);
        }

        if (ExecutablePolicy.CheckPath(finalPath) is var again && again != ExecutableRefusal.None)
        {
            return ExecutableCheck.Refused(again);
        }

        if (_protectedFolders.Value.Any(f => finalPath.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
        {
            return ExecutableCheck.Refused(ExecutableRefusal.ProtectedLocation);
        }

        var size = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
        if (size > AppRules.MaxExecutableBytes)
        {
            return ExecutableCheck.Refused(ExecutableRefusal.TooLarge);
        }

        var header = new byte[ExecutablePolicy.HeaderBytes];
        var read = RandomAccess.Read(opened.File, header, 0);
        if (!ExecutablePolicy.IsLaunchableImage(header.AsSpan(0, read)))
        {
            return ExecutableCheck.Refused(ExecutableRefusal.NotPortableExecutable);
        }

        var identity = new ExecutableIdentity(id.VolumeSerialNumber.ToString("x16"), FileIdHex(id), size, "");
        if (_self.Value.Image is { } self && self.SameFile(identity))
        {
            return ExecutableCheck.Refused(ExecutableRefusal.ProtectedLocation);
        }

        // Supplementary: a renamed copy of a denied program still carries its original name in the version resource.
        // When exclusive (registration and launch), the folders and the file are held, so this path read refers to the
        // same file; for the cheap listing check it is advisory only.
        try
        {
            var version = FileVersionInfo.GetVersionInfo(finalPath);
            if (ExecutablePolicy.IsDeniedName(version.OriginalFilename) || ExecutablePolicy.IsDeniedName(version.InternalName))
            {
                return ExecutableCheck.Refused(ExecutableRefusal.DeniedProgram);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return ExecutableCheck.Refused(ExecutableRefusal.Unreadable);
        }

        return new ExecutableCheck(ExecutableRefusal.None, finalPath, identity);
    }

    private static ExecutableRefusal OpenFailure(int error) =>
        error is ErrorFileNotFound or ErrorPathNotFound ? ExecutableRefusal.NotFound : ExecutableRefusal.Unreadable;

    /// <summary>Final path in plain <c>X:\...</c> form, or null when it is not on a drive letter (UNC, volume GUID).</summary>
    private static string? FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[1024];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0 /* FILE_NAME_NORMALIZED | VOLUME_NAME_DOS */);
        if (length == 0 || length >= buffer.Length)
        {
            return null;
        }

        var text = new string(buffer, 0, (int)length);
        return text.StartsWith(@"\\?\", StringComparison.Ordinal) && text.Length > 7 && text[5] == ':' && text[6] == '\\' ? text[4..] : null;
    }

    private static string FileIdHex(FileIdInfo id)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, id.FileIdLow);
        BitConverter.TryWriteBytes(bytes[8..], id.FileIdHigh);
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>SHA-256 of exactly <paramref name="size"/> bytes read through the handle; cancellable between blocks.</summary>
    private static string Hash(SafeFileHandle file, long size, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long offset = 0;
        while (offset < size)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = RandomAccess.Read(file, buffer.AsSpan(0, (int)Math.Min(buffer.Length, size - offset)), offset);
            if (read <= 0)
            {
                throw new ToolFailureException(ErrorCodes.OsApiError, "The executable could not be read completely. Nothing was launched.", retryable: true);
            }

            hash.AppendData(buffer, 0, read);
            offset += read;
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>The Windows directory and Sysacta's own folder, as final paths with a trailing separator.</summary>
    private static string[] ProtectedFolders() =>
        [.. new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows), AppContext.BaseDirectory }
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => FolderFinalPath(p.TrimEnd('\\')) ?? p.TrimEnd('\\'))
            .Select(p => p + "\\")];

    private static string? FolderFinalPath(string folder)
    {
        using var handle = CreateFileW(folder, FileReadAttributes, FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        return handle.IsInvalid ? null : FinalPath(handle);
    }

    // ---- Processes ----

    public ProcessScan ScanSession(IReadOnlyList<AppMatchKey> apps, CancellationToken cancellationToken)
    {
        var self = _self.Value;
        var processes = new List<ProcessFacts>();
        var uncertain = new HashSet<string>(StringComparer.Ordinal);
        var failed = 0;
        var (entries, truncated) = Snapshot();
        if (truncated)
        {
            // Processes beyond the limit were not inspected: none of the apps can be known to be (not) running.
            uncertain.UnionWith(apps.Select(a => a.AppId));
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Pid == 0 || !ProcessIdToSessionId(entry.Pid, out var session) || session != self.Session)
            {
                continue;
            }

            var candidates = apps.Where(a => string.Equals(a.FileName, entry.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            using var handle = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, entry.Pid);
            var imageUnknown = false;
            var facts = handle.IsInvalid ? null : Inspect(handle, (int)entry.Pid, entry.Name, candidates, self, out imageUnknown);
            if (facts is null || facts.SameUser is null)
            {
                failed++;
                uncertain.UnionWith(candidates.Select(c => c.AppId));
                continue;
            }

            if (imageUnknown)
            {
                uncertain.UnionWith(candidates.Select(c => c.AppId));
            }

            if (facts.SameUser == true)
            {
                processes.Add(facts);
            }
        }

        return new ProcessScan(processes, failed, uncertain, truncated);
    }

    public IOpenedProcess OpenProcess(int pid, long creationTime, IReadOnlyList<AppMatchKey> apps, bool terminate)
    {
        var self = _self.Value;
        if (pid <= 0 || !ProcessIdToSessionId((uint)pid, out var session) || session != self.Session)
        {
            throw new ToolFailureException(ErrorCodes.TargetNotFound, "No such process of the current user and session is running. Nothing was changed.");
        }

        var handle = NativeMethods.OpenProcess(ProcessQueryLimitedInformation | Synchronize | (terminate ? ProcessTerminate : 0), false, (uint)pid);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw error == ErrorInvalidParameter
                ? new ToolFailureException(ErrorCodes.TargetNotFound, "The process has exited. Nothing was changed.")
                : new ToolFailureException(ErrorCodes.PermissionDenied, "Windows does not allow Sysacta to open this process. Nothing was changed.");
        }

        try
        {
            if (!GetProcessTimes(handle, out var creation, out _, out _, out _))
            {
                throw new ToolFailureException(ErrorCodes.OsApiError, "The process could not be inspected. Nothing was changed.");
            }

            if (creation != creationTime)
            {
                throw new ToolFailureException(ErrorCodes.TargetChanged, "The pid now belongs to another process. Nothing was changed.");
            }

            if (WaitForSingleObject(handle, 0) == WaitObject0)
            {
                throw new ToolFailureException(ErrorCodes.TargetNotFound, "The process has exited. Nothing was changed.");
            }

            var name = ImagePath(handle) is { } image ? Path.GetFileName(image) : "";
            var candidates = apps.Where(a => string.Equals(a.FileName, name, StringComparison.OrdinalIgnoreCase)).ToList();
            var facts = Inspect(handle, pid, name, candidates, self, out _)
                ?? throw new ToolFailureException(ErrorCodes.OsApiError, "The process could not be inspected. Nothing was changed.");
            return new OpenedProcess(handle, facts);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads every fact through <paramref name="handle"/>. Null when the creation time cannot be read. The image file is
    /// opened only for processes whose name equals a candidate app's file name; <paramref name="imageUnknown"/> is true
    /// when that could not be completed.
    /// </summary>
    private static ProcessFacts? Inspect(SafeProcessHandle handle, int pid, string name, List<AppMatchKey> candidates, SelfInfo self, out bool imageUnknown)
    {
        imageUnknown = false;
        if (!GetProcessTimes(handle, out var creation, out _, out _, out _) || creation <= 0)
        {
            return null;
        }

        var (sameUser, rid) = ReadToken(handle, self.User);
        bool? isProtected = GetProcessInformation(handle, ProcessProtectionLevelInfo, out var level, sizeof(uint)) ? level != ProtectionLevelNone : null;
        bool? critical = IsProcessCritical(handle, out var c) ? c : null;
        string? appId = null;
        if (candidates.Count > 0)
        {
            var image = ImageIdentity(handle, creation);
            imageUnknown = image is null;
            appId = image is null ? null : candidates.FirstOrDefault(a => a.Identity.SameFile(image))?.AppId;
        }

        return new ProcessFacts(pid, creation, name, sameUser, rid, isProtected, critical,
            IsSelf: pid == self.Pid && creation == self.Creation,
            IsParent: self.ParentPid is { } parent && pid == parent && creation == self.ParentCreation,
            appId);
    }

    private static (bool? SameUser, int? Rid) ReadToken(SafeProcessHandle process, SecurityIdentifier? user)
    {
        if (!OpenProcessToken(process, TokenQuery, out var token))
        {
            return (null, null);
        }

        using (token)
        {
            var owner = ReadSid(token, TokenUser);
            var label = ReadSid(token, TokenIntegrityLevel);
            var rid = IntegrityRid(label);
            return (owner is null || user is null ? null : owner.Equals(user), rid);
        }
    }

    /// <summary>The SID of TOKEN_USER / TOKEN_MANDATORY_LABEL (both start with SID_AND_ATTRIBUTES).</summary>
    private static SecurityIdentifier? ReadSid(SafeAccessTokenHandle token, int infoClass)
    {
        _ = GetTokenInformation(token, infoClass, IntPtr.Zero, 0, out var length);
        if (length <= 0 || length > 4096)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            return GetTokenInformation(token, infoClass, buffer, length, out _) ? new SecurityIdentifier(Marshal.ReadIntPtr(buffer)) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The RID of a mandatory label SID (S-1-16-RID), e.g. 0x2000 for medium.</summary>
    private static int? IntegrityRid(SecurityIdentifier? label) =>
        label is not null && label.Value.StartsWith("S-1-16-", StringComparison.Ordinal) && int.TryParse(label.Value[7..], out var rid) ? rid : null;

    private static string? ImagePath(SafeProcessHandle handle, bool native = false)
    {
        var buffer = new char[1024];
        var size = (uint)buffer.Length;
        return QueryFullProcessImageNameW(handle, native ? 1u /* PROCESS_NAME_NATIVE */ : 0, buffer, ref size) && size > 0 && size < buffer.Length ? new string(buffer, 0, (int)size) : null;
    }

    /// <summary>
    /// Volume + file id of the file at the image path the process was created from, only when that file provably is the
    /// loaded image; null ("unknown") otherwise. Windows offers no documented way to identify the mapped image file from a
    /// limited-rights process handle, and the image path keeps its creation-time name: a running exe can be renamed and
    /// another file put at that path (an ordinary in-place update does exactly that). So the file now at the path counts
    /// only when its ChangeTime is earlier than the process creation: renaming, moving, linking or writing a file updates
    /// its ChangeTime, so a file unchanged since before the process started was already that path's file when the image
    /// was loaded. The NT path is used (no drive letter mapping) and opened without following reparse points.
    /// </summary>
    private static ExecutableIdentity? ImageIdentity(SafeProcessHandle handle, long creation) =>
        ImagePath(handle, native: true) is { } path && path.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase)
            ? FileKey(@"\\?\GLOBALROOT" + path, noReparse: true, unchangedSince: creation)
            : null;

    /// <summary>Volume + file id of a file by path (attribute-only open: no sharing conflict). Size and hash are placeholders.</summary>
    private static ExecutableIdentity? FileKey(string path, bool noReparse = false, long? unchangedSince = null)
    {
        using var file = CreateFileW(path, FileReadAttributes, FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting, noReparse ? FileFlagOpenReparsePoint : 0, IntPtr.Zero);
        return !file.IsInvalid
            && (!noReparse || (GetFileInformationByHandle(file, out var info) && (info.FileAttributes & (FileAttributeReparsePoint | FileAttributeDirectory)) == 0))
            && (unchangedSince is not { } since
                || (GetFileBasicInformationByHandleEx(file, FileBasicInfoClass, out var basic, (uint)Marshal.SizeOf<FileBasicInfo>()) && basic.ChangeTime > 0 && basic.ChangeTime < since))
            && GetFileInformationByHandleEx(file, FileIdInfoClass, out var id, (uint)Marshal.SizeOf<FileIdInfo>())
            ? new ExecutableIdentity(id.VolumeSerialNumber.ToString("x16"), FileIdHex(id), 1, "")
            : null;
    }

    private static (List<(uint Pid, uint ParentPid, string Name)> Entries, bool Truncated) Snapshot()
    {
        using var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot.IsInvalid)
        {
            throw new ToolFailureException(ErrorCodes.OsApiError, "The process list could not be read.", retryable: true);
        }

        var entries = new List<(uint, uint, string)>();
        var entry = new ProcessEntry32W { Size = (uint)Marshal.SizeOf<ProcessEntry32W>() };
        for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
        {
            if (entries.Count == MaxSnapshot)
            {
                return (entries, true);
            }

            entries.Add((entry.ProcessId, entry.ParentProcessId, entry.ExeFile ?? ""));
        }

        // Only ERROR_NO_MORE_FILES is the normal end; any other failure means the list is incomplete.
        if (Marshal.GetLastWin32Error() != ErrorNoMoreFiles)
        {
            throw new ToolFailureException(ErrorCodes.OsApiError, "The process list could not be read completely.", retryable: true);
        }

        return (entries, false);
    }

    private sealed record SelfInfo(int Pid, long Creation, uint Session, SecurityIdentifier? User, int? ParentPid, long ParentCreation, ExecutableIdentity? Image);

    private static SelfInfo DetectSelf()
    {
        var pid = Environment.ProcessId;
        if (!ProcessIdToSessionId((uint)pid, out var session))
        {
            throw new ToolFailureException(ErrorCodes.OsApiError, "The current session could not be determined.", retryable: true);
        }

        using var identity = WindowsIdentity.GetCurrent();
        long creation = 0;
        using (var own = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid))
        {
            if (!own.IsInvalid)
            {
                _ = GetProcessTimes(own, out creation, out _, out _, out _);
            }
        }

        // The parent pid is valid only while that process is older than Sysacta (otherwise the pid was reused).
        int? parentPid = null;
        long parentCreation = 0;
        if (Snapshot().Entries.FirstOrDefault(e => e.Pid == pid) is { ParentPid: > 0 } me) // throws when unreadable: retried later
        {
            using var parent = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, me.ParentPid);
            if (!parent.IsInvalid && GetProcessTimes(parent, out var pc, out _, out _, out _) && pc > 0 && pc <= creation)
            {
                parentPid = (int)me.ParentPid;
                parentCreation = pc;
            }
        }

        // Without its own file identity Sysacta could not refuse a copy of itself: fail (and retry on the next call).
        var image = (Environment.ProcessPath is { } exe ? FileKey(exe) : null)
            ?? throw new ToolFailureException(ErrorCodes.OsApiError, "Sysacta could not identify its own executable.", retryable: true);
        return new SelfInfo(pid, creation, session, identity.User, parentPid, parentCreation, image);
    }

    /// <summary>Elevated or above medium integrity. Unreadable counts as elevated (launching stays disabled).</summary>
    private static bool DetectElevated()
    {
        using var own = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, (uint)Environment.ProcessId);
        if (own.IsInvalid || !OpenProcessToken(own, TokenQuery, out var token))
        {
            return true;
        }

        using (token)
        {
            var label = ReadSid(token, TokenIntegrityLevel);
            var buffer = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                var elevated = !GetTokenInformation(token, TokenElevation, buffer, sizeof(int), out _) || Marshal.ReadInt32(buffer) != 0;
                var rid = IntegrityRid(label) ?? int.MaxValue;
                return elevated || rid >= 0x3000;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private sealed class OpenedExecutable : IDisposable
    {
        public List<SafeFileHandle> Folders { get; } = [];
        public SafeFileHandle File { get; set; } = new(IntPtr.Zero, false);

        public void Dispose()
        {
            File.Dispose();
            foreach (var folder in Folders)
            {
                folder.Dispose();
            }
        }
    }

    /// <summary>NT path (\Device\...) of an open handle; independent of drive letter mappings.</summary>
    private static string? NtPath(SafeFileHandle handle)
    {
        var buffer = new char[1024];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 2 /* FILE_NAME_NORMALIZED | VOLUME_NAME_NT */);
        return length == 0 || length >= buffer.Length ? null : new string(buffer, 0, (int)length);
    }

    private sealed class PreparedLaunch(OpenedExecutable opened, string path, string expectedImage, char[] commandLine) : IPreparedLaunch
    {
        /// <summary>Upper bound for confirming that a suspended process this call created was removed.</summary>
        private const uint ReclaimWaitMs = 2000;
        private int _started;

        public LaunchResult Start(Func<ToolError?> beforeResume)
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
            {
                throw new InvalidOperationException("A prepared launch can start only once.");
            }

            // lpApplicationName is the verified path: the program never comes from parsing the command line or PATH.
            // No inherited handles, no std handles, Sysacta's own environment, the app's folder as working directory.
            var startup = new StartupInfoW { Cb = Marshal.SizeOf<StartupInfoW>() };
            if (!CreateProcessW(path, commandLine, IntPtr.Zero, IntPtr.Zero, false, CreateSuspended | CreateDefaultErrorMode,
                IntPtr.Zero, Path.GetDirectoryName(path)!, ref startup, out var info))
            {
                return new LaunchResult(Marshal.GetLastWin32Error() == ErrorElevationRequired ? LaunchStatus.ElevationRequired : LaunchStatus.Failed);
            }

            var pid = (int)info.ProcessId;
            using var process = new SafeProcessHandle(info.Process, ownsHandle: true);
            var resumed = false;
            var reclaimAttempted = false;
            try
            {
                // Defense in depth: before any of its code runs, the suspended process's image must be the held file, compared
                // as NT device paths (not through drive letters, which a same-user process could remap).
                if (!string.Equals(ImagePath(process, native: true), expectedImage, StringComparison.OrdinalIgnoreCase))
                {
                    reclaimAttempted = true;
                    return new LaunchResult(LaunchStatus.ImageMismatch, pid, Reclaimed: Reclaim(process));
                }

                if (!GetProcessTimes(process, out var creation, out _, out _, out _))
                {
                    reclaimAttempted = true;
                    return new LaunchResult(LaunchStatus.ResumeFailed, pid, Reclaimed: Reclaim(process));
                }

                // The last check before the process may run: cancellation, executor detachment, authorization, approval.
                if (beforeResume() is { } refusal)
                {
                    reclaimAttempted = true;
                    return new LaunchResult(LaunchStatus.Withdrawn, pid, creation, Reclaim(process), refusal);
                }

                if (ResumeThread(info.Thread) == uint.MaxValue)
                {
                    reclaimAttempted = true;
                    return new LaunchResult(LaunchStatus.ResumeFailed, pid, creation, Reclaim(process));
                }

                resumed = true;
                return new LaunchResult(LaunchStatus.Started, pid, creation);
            }
            finally
            {
                if (!resumed && !reclaimAttempted)
                {
                    // An exception before the resume: never leave a suspended process this call created behind (the
                    // executor reports the call as unknown).
                    _ = Reclaim(process);
                }

                _ = CloseHandle(info.Thread);
            }
        }

        /// <summary>Terminates the never-resumed process this call created, through its own handle; true when its exit is confirmed.</summary>
        private static bool Reclaim(SafeProcessHandle process) =>
            TerminateProcess(process, TerminatedExitCode) && WaitForSingleObject(process, ReclaimWaitMs) == WaitObject0;

        public void Dispose() => opened.Dispose();
    }

    private sealed class OpenedProcess(SafeProcessHandle handle, ProcessFacts facts) : IOpenedProcess
    {
        private IntPtr _window;

        public ProcessFacts Facts { get; } = facts;

        public int FindCloseableWindows()
        {
            var found = new List<IntPtr>();
            EnumWindowsProc callback = (window, _) =>
            {
                if (IsCloseable(window))
                {
                    found.Add(window);
                }

                return found.Count < 2;
            };
            _ = EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            _window = found.Count == 1 ? found[0] : IntPtr.Zero;
            return found.Count;
        }

        public CloseRequestStatus RequestClose()
        {
            // The handle keeps the pid from being reused, so window ownership by this pid means this process.
            if (_window == IntPtr.Zero || !IsWindow(_window) || !IsCloseable(_window))
            {
                return CloseRequestStatus.WindowGone;
            }

            return PostMessageW(_window, WmClose, IntPtr.Zero, IntPtr.Zero) ? CloseRequestStatus.Posted : CloseRequestStatus.Failed;
        }

        public bool Terminate() => TerminateProcess(handle, TerminatedExitCode);

        public bool WaitForExit(TimeSpan timeout) => WaitForSingleObject(handle, (uint)timeout.TotalMilliseconds) == WaitObject0;

        public void Dispose() => handle.Dispose();

        private bool IsCloseable(IntPtr window) =>
            GetWindowThreadProcessId(window, out var owner) != 0 && owner == (uint)Facts.Pid
            && IsWindowVisible(window) && GetWindow(window, GwOwner) == IntPtr.Zero
            && (GetWindowLongPtrW(window, GwlExStyle).ToInt64() & WsExToolWindow) == 0;
    }
}
