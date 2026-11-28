using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Apps;
using Sysacta.Windows.Apps;

namespace Sysacta.Windows.Tests;

/// <summary>
/// Phase 2A adapter checks on the machine running the tests. The only process ever launched or terminated is the
/// dedicated HarmlessTarget built with the tests, from a private copy in a test folder, by the exact pid and creation
/// time this test obtained. Existing processes and real apps are only read (a system exe is inspected, never run).
/// </summary>
public sealed class WindowsAppPlatformTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly WindowsAppPlatform _os = new();
    private readonly string _dir;
    /// <summary>Processes this test created: removed by exact pid + creation time through their own handle.</summary>
    private readonly List<(int Pid, long Creation)> _launched = [];

    public WindowsAppPlatformTests()
    {
        // Next to the test output (a local fixed drive, long path names), never inside the test's own folder, which
        // counts as Sysacta's folder for this process.
        _dir = Path.Combine(Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd('\\', '/'))!, "apptest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        foreach (var (pid, creation) in _launched)
        {
            try
            {
                using var process = _os.OpenProcess(pid, creation, [], terminate: true);
                _ = process.Terminate();
                _ = process.WaitForExit(TimeSpan.FromSeconds(5));
            }
            catch (ToolFailureException)
            {
                // Already gone (or the pid now belongs to another process, which is never touched).
            }
        }

        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void RequireWindows() => Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");

    /// <summary>A private copy of the harmless target (apphost + its files) in a fresh folder; returns its exe path.</summary>
    private string CopyTarget(string folder = "target")
    {
        var source = typeof(WindowsAppPlatformTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "HarmlessTargetDirectory").Value!;
        var destination = Path.Combine(_dir, folder);
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        var exe = Path.Combine(destination, "HarmlessTarget.exe");
        Assert.True(File.Exists(exe));
        return exe;
    }

    private RegisteredApp Approve(string path, params string[] args)
    {
        var check = _os.Inspect(path, computeHash: true, Ct);
        Assert.Equal(ExecutableRefusal.None, check.Refusal);
        return new RegisteredApp(AppRules.NewAppId(), "Harmless", check.CanonicalPath!, args, true, 1, check.Identity!);
    }

    [Fact]
    public void Native_struct_layouts_match_the_SDK()
    {
        Assert.Equal(52, Marshal.SizeOf<NativeMethods.ByHandleFileInformation>());
        Assert.Equal(24, Marshal.SizeOf<NativeMethods.FileIdInfo>());
        Assert.Equal(568, Marshal.SizeOf<NativeMethods.ProcessEntry32W>());
        Assert.Equal(104, Marshal.SizeOf<NativeMethods.StartupInfoW>());
        Assert.Equal(24, Marshal.SizeOf<NativeMethods.ProcessInformation>());
    }

    [Fact]
    public void Inspection_identifies_the_file_and_hashes_only_when_asked()
    {
        RequireWindows();
        var exe = CopyTarget();
        var cheap = _os.Inspect(exe, computeHash: false, Ct);
        Assert.Equal(ExecutableRefusal.None, cheap.Refusal);
        Assert.Equal(exe, cheap.CanonicalPath, ignoreCase: true);
        Assert.Equal("", cheap.Identity!.Sha256);
        var full = _os.Inspect(exe.ToUpperInvariant(), computeHash: true, Ct);
        Assert.True(full.Identity!.IsWellFormed());
        Assert.True(full.Identity.SameFile(cheap.Identity));
        Assert.Equal(new FileInfo(exe).Length, full.Identity.Size);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(exe))), full.Identity.Sha256);
    }

    [Fact]
    public void Non_executables_system_programs_and_links_are_refused()
    {
        RequireWindows();
        var fake = Path.Combine(_dir, "fake.exe");
        File.WriteAllText(fake, "MZ not really a program");
        Assert.Equal(ExecutableRefusal.NotPortableExecutable, _os.Inspect(fake, false, Ct).Refusal);
        var dll = Path.Combine(_dir, "library.exe");
        // A native DLL renamed to .exe (a .NET "exe" assembly built as .dll has no DLL flag, so it is not used here).
        File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), dll);
        Assert.Equal(ExecutableRefusal.NotPortableExecutable, _os.Inspect(dll, false, Ct).Refusal);
        Assert.Equal(ExecutableRefusal.NotFound, _os.Inspect(Path.Combine(_dir, "missing.exe"), false, Ct).Refusal);
        Assert.Equal(ExecutableRefusal.ProtectedLocation, _os.Inspect(Path.Combine(Environment.SystemDirectory, "whoami.exe"), false, Ct).Refusal);
        Assert.Equal(ExecutableRefusal.DeniedProgram, _os.Inspect(Path.Combine(Environment.SystemDirectory, "cmd.exe"), false, Ct).Refusal);

        // A renamed copy of the target keeps its version resource name, which is not denied; a renamed copy of a denied
        // program would be refused by that name (covered in Core). Here: the same bytes under another name are another file.
        var copy = Path.Combine(_dir, "copy.exe");
        File.Copy(CopyTarget("second"), copy);
        Assert.False(_os.Inspect(copy, false, Ct).Identity!.SameFile(_os.Inspect(Path.Combine(_dir, "second", "HarmlessTarget.exe"), false, Ct).Identity!));
    }

    [Fact]
    public void Paths_through_symbolic_links_are_refused()
    {
        RequireWindows();
        var exe = CopyTarget();
        var link = Path.Combine(_dir, "link");
        try
        {
            Directory.CreateSymbolicLink(link, Path.GetDirectoryName(exe)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Creating a symbolic link requires developer mode or administrator rights.");
        }

        Assert.Equal(ExecutableRefusal.ReparsePoint, _os.Inspect(Path.Combine(link, "HarmlessTarget.exe"), false, Ct).Refusal);
    }

    [Fact]
    public void A_prepared_launch_locks_the_file_and_its_folders_and_detects_changes()
    {
        RequireWindows();
        var exe = CopyTarget();
        var app = Approve(exe);
        using (_os.PrepareLaunch(app, Ct))
        {
            Assert.ThrowsAny<IOException>(() => File.OpenWrite(exe).Dispose());
            Assert.ThrowsAny<IOException>(() => File.Move(exe, exe + ".old"));
            Assert.ThrowsAny<Exception>(() => Directory.Move(Path.GetDirectoryName(exe)!, Path.Combine(_dir, "moved")));
        }

        // Released: the folder can be renamed again (and back).
        Directory.Move(Path.GetDirectoryName(exe)!, Path.Combine(_dir, "moved"));
        Directory.Move(Path.Combine(_dir, "moved"), Path.GetDirectoryName(exe)!);

        // Same file id, other content: refused by the hash.
        using (var stream = new FileStream(exe, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Seek(-1, SeekOrigin.End);
            var last = stream.ReadByte();
            stream.Seek(-1, SeekOrigin.End);
            stream.WriteByte((byte)(last ^ 0xFF));
        }

        var changed = Assert.Throws<ToolFailureException>(() => _os.PrepareLaunch(app, Ct));
        Assert.Equal(ErrorCodes.TargetChanged, changed.Code);

        // Replaced by another file at the same path: refused by the file id.
        File.Delete(exe);
        File.Copy(CopyTarget("other") , exe);
        Assert.Equal(ErrorCodes.TargetChanged, Assert.Throws<ToolFailureException>(() => _os.PrepareLaunch(app, Ct)).Code);
    }

    [Fact]
    public void The_harmless_target_is_launched_found_and_terminated_through_its_own_handle()
    {
        RequireWindows();
        string[] fixedArgs = ["a b", "", "c\"d", @"tail\", "日本語", "&|<>^%"];
        var app = Approve(CopyTarget(), fixedArgs);
        LaunchResult result;
        using (var prepared = _os.PrepareLaunch(app, Ct))
        {
            result = prepared.Start(() => null);
        }

        Assert.Equal(LaunchStatus.Started, result.Status);
        _launched.Add((result.Pid!.Value, result.CreationTime!.Value));

        // The approved fixed arguments arrive exactly as approved (through CreateProcessW and the C runtime's parsing).
        var echo = Path.Combine(Path.GetDirectoryName(app.Path)!, "args.txt");
        for (var i = 0; i < 100 && !File.Exists(echo); i++)
        {
            Thread.Sleep(50);
        }

        Thread.Sleep(200);
        Assert.Equal(fixedArgs, File.ReadAllLines(echo));
        var key = AppMatchKey.For(app);
        var scan = _os.ScanSession([key], Ct);
        var facts = Assert.Single(scan.Processes, p => p.AppId == app.Id);
        Assert.Equal((result.Pid, result.CreationTime), (facts.Pid, (long?)facts.CreationTime));
        Assert.Equal("HarmlessTarget.exe", facts.Name, ignoreCase: true);
        Assert.Equal(_os.IsElevated ? TerminateBlock.Elevated : null, ProcessRules.Evaluate(facts));

        Assert.Equal(ErrorCodes.TargetChanged, Assert.Throws<ToolFailureException>(() => _os.OpenProcess(facts.Pid, facts.CreationTime + 1, [key], terminate: true)).Code);
        using (var process = _os.OpenProcess(facts.Pid, facts.CreationTime, [key], terminate: true))
        {
            Assert.Equal(app.Id, process.Facts.AppId);
            Assert.Equal(0, process.FindCloseableWindows());
            Assert.Equal(CloseRequestStatus.WindowGone, process.RequestClose());
            Assert.True(process.Terminate());
            Assert.True(process.WaitForExit(TimeSpan.FromSeconds(5)));
        }

        Assert.Equal(ErrorCodes.TargetNotFound, Assert.Throws<ToolFailureException>(() => _os.OpenProcess(facts.Pid, facts.CreationTime, [key], terminate: true)).Code);
        Assert.DoesNotContain(_os.ScanSession([key], Ct).Processes, p => p.AppId == app.Id);
    }

    [Theory]
    [InlineData("close", true)]
    [InlineData("refuse-close", false)]
    public void A_normal_close_request_reaches_the_single_window_and_never_forces(string mode, bool exits)
    {
        RequireWindows();
        var app = Approve(CopyTarget(), "--window", mode);
        LaunchResult result;
        using (var prepared = _os.PrepareLaunch(app, Ct))
        {
            result = prepared.Start(() => null);
        }

        Assert.Equal(LaunchStatus.Started, result.Status);
        _launched.Add((result.Pid!.Value, result.CreationTime!.Value));
        using var process = _os.OpenProcess(result.Pid.Value, result.CreationTime!.Value, [AppMatchKey.For(app)], terminate: true);
        var windows = 0;
        for (var i = 0; i < 100 && (windows = process.FindCloseableWindows()) == 0; i++)
        {
            Thread.Sleep(50);
        }

        Assert.Equal(1, windows);
        Assert.Equal(CloseRequestStatus.Posted, process.RequestClose());
        // A window that ignores WM_CLOSE (a pending save prompt) keeps running: accepted, not exited, nothing forced.
        Assert.Equal(exits, process.WaitForExit(TimeSpan.FromSeconds(2)));
        if (!exits)
        {
            Assert.True(process.Terminate());
            Assert.True(process.WaitForExit(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public void A_launch_refused_before_resume_never_runs_and_is_reclaimed()
    {
        RequireWindows();
        var app = Approve(CopyTarget(), "x");
        var echo = Path.Combine(Path.GetDirectoryName(app.Path)!, "args.txt");
        LaunchResult result;
        using (var prepared = _os.PrepareLaunch(app, Ct))
        {
            result = prepared.Start(() => new ToolError(ErrorCodes.Cancelled, "refused", false));
        }

        Assert.Equal((LaunchStatus.Withdrawn, true, ErrorCodes.Cancelled), (result.Status, result.Reclaimed, result.Refusal!.Code));
        Assert.Equal(ErrorCodes.TargetNotFound, Assert.Throws<ToolFailureException>(() => _os.OpenProcess(result.Pid!.Value, result.CreationTime!.Value, [AppMatchKey.For(app)], terminate: false)).Code);
        Thread.Sleep(300);
        Assert.False(File.Exists(echo)); // its code never ran
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_running_old_image_is_not_matched_to_a_replacement_approved_later(bool renameOlderFileIntoPlace)
    {
        RequireWindows();
        var exe = CopyTarget();
        var older = Path.Combine(Path.GetDirectoryName(exe)!, "older.bin");
        File.Copy(exe, older); // a separate file that exists before the process starts
        var app = Approve(exe);
        LaunchResult result;
        using (var prepared = _os.PrepareLaunch(app, Ct))
        {
            result = prepared.Start(() => null);
        }

        Assert.Equal(LaunchStatus.Started, result.Status);
        _launched.Add((result.Pid!.Value, result.CreationTime!.Value));
        Assert.Contains(_os.ScanSession([AppMatchKey.For(app)], Ct).Processes, p => p.Pid == result.Pid && p.AppId == app.Id);

        // An in-place update while the old image keeps running: the running exe is renamed away and another file takes
        // its path (a new copy, or an older file renamed into place), and the local user approves that file.
        File.Move(exe, exe + ".old");
        if (renameOlderFileIntoPlace)
        {
            File.Move(older, exe);
        }
        else
        {
            File.Copy(exe + ".old", exe);
        }

        var replacement = Approve(exe);
        Assert.False(replacement.Identity.SameFile(app.Identity));
        var scan = _os.ScanSession([AppMatchKey.For(replacement)], Ct);
        var old = Assert.Single(scan.Processes, p => p.Pid == result.Pid);
        Assert.Null(old.AppId);
        Assert.Contains(replacement.Id, scan.UncertainAppIds);
        using var opened = _os.OpenProcess(old.Pid, old.CreationTime, [AppMatchKey.For(replacement)], terminate: true);
        Assert.Null(opened.Facts.AppId);
        Assert.NotNull(ProcessRules.Evaluate(opened.Facts));
    }

    [Fact]
    public void Sysacta_itself_is_listed_with_its_exact_creation_time_and_marked_self()
    {
        RequireWindows();
        var scan = _os.ScanSession([], Ct);
        using var me = Process.GetCurrentProcess();
        var self = Assert.Single(scan.Processes, p => p.Pid == Environment.ProcessId);
        Assert.True(self.IsSelf);
        Assert.Equal(TerminateBlock.Self, ProcessRules.Evaluate(self));
        Assert.Equal(me.StartTime.ToUniversalTime().ToFileTimeUtc(), self.CreationTime);
        Assert.All(scan.Processes, p => Assert.True(p.SameUser));
        Assert.False(scan.Truncated);
    }

    [Fact]
    public void Command_lines_round_trip_through_CommandLineToArgvW()
    {
        RequireWindows();
        string[] args = ["", "a b", "\"", @"C:\dir\", @"x\\""y", "日本語", "&|<>^%", "tab\there", @"\\"];
        var line = ExecutablePolicy.BuildCommandLine(@"C:\Program Files\App\app.exe", args);
        var argv = CommandLineToArgvW(line, out var count);
        try
        {
            var parsed = Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!).ToArray();
            Assert.Equal([@"C:\Program Files\App\app.exe", .. args], parsed);
        }
        finally
        {
            _ = LocalFree(argv);
        }
    }

    [Fact]
    public async Task Start_menu_discovery_only_reads_and_finishes()
    {
        RequireWindows();
        var result = await StartMenuDiscovery.ScanAsync(_os, Ct);
        Assert.True(result.Candidates.Count <= StartMenuDiscovery.MaxResults);
        Assert.All(result.Candidates, c => Assert.EndsWith(".exe", c.Target, StringComparison.OrdinalIgnoreCase));
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
