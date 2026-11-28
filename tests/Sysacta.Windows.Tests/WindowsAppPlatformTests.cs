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
    private readonly List<int> _launched = [];

    public WindowsAppPlatformTests()
    {
        // Next to the test output (a local fixed drive, long path names), never inside the test's own folder, which
        // counts as Sysacta's folder for this process.
        _dir = Path.Combine(Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd('\\', '/'))!, "apptest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        foreach (var pid in _launched)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.ProcessName.Equals("HarmlessTarget", StringComparison.OrdinalIgnoreCase))
                {
                    p.Kill();
                    p.WaitForExit(5000);
                }
            }
            catch (ArgumentException)
            {
                // Already gone.
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
        var app = Approve(CopyTarget(), "a b", "", "c\"d");
        LaunchResult result;
        using (var prepared = _os.PrepareLaunch(app, Ct))
        {
            result = prepared.Start();
        }

        Assert.Equal(LaunchStatus.Started, result.Status);
        _launched.Add(result.Pid!.Value);
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
