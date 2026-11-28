using System.Text;
using Sysacta.Core.Tools.Apps;

namespace Sysacta.Core.Tests;

/// <summary>Phase 2A: OS-independent executable rules, argv quoting, start-time format and allow-list limits.</summary>
public class AppPolicyTests
{
    [Theory]
    [InlineData(@"C:\Program Files\App\app.exe")]
    [InlineData(@"D:\Tools\ゲーム\起動.exe")]
    [InlineData(@"c:\a\b.EXE")]
    public void Plain_absolute_local_exe_paths_pass(string path) => Assert.Equal(ExecutableRefusal.None, ExecutablePolicy.CheckPath(path));

    [Theory]
    [InlineData(@"app.exe")]
    [InlineData(@"Program Files\app.exe")]
    [InlineData(@"\\server\share\app.exe")]
    [InlineData(@"\\?\C:\app\app.exe")]
    [InlineData(@"\\.\C:\app\app.exe")]
    [InlineData(@"C:app.exe")]
    [InlineData(@"C:/apps/app.exe")]
    [InlineData(@"C:\apps\app.exe:stream")]
    [InlineData(@"C:\apps\app.exe::$DATA")]
    [InlineData(@"C:\apps\..\app.exe")]
    [InlineData(@"C:\apps\.\app.exe")]
    [InlineData(@"C:\apps\\app.exe")]
    [InlineData(@"C:\apps.\app.exe")]
    [InlineData(@"C:\apps \app.exe")]
    [InlineData(@"C:\PROGRA~1\app.exe")]
    [InlineData(@"C:\%ProgramFiles%\app.exe")]
    [InlineData(@"C:\apps\a*.exe")]
    [InlineData("C:\\apps\\\"app.exe")]
    [InlineData("C:\\apps\\app\n.exe")]
    [InlineData("C:\\apps\\app\u202E.exe")]
    [InlineData(@"C:\CON\app.exe")]
    [InlineData(@"C:\apps\nul.exe")]
    [InlineData(@"C:\apps\app.exe.")]
    public void Ambiguous_or_non_local_paths_are_refused(string path) =>
        Assert.Equal(ExecutableRefusal.InvalidPath, ExecutablePolicy.CheckPath(path));

    [Fact]
    public void Overlong_paths_are_refused() =>
        Assert.Equal(ExecutableRefusal.InvalidPath, ExecutablePolicy.CheckPath(@"C:\" + new string('a', 253) + ".exe"));

    [Theory]
    [InlineData(@"C:\apps\run.lnk")]
    [InlineData(@"C:\apps\run.bat")]
    [InlineData(@"C:\apps\run.cmd")]
    [InlineData(@"C:\apps\run.ps1")]
    [InlineData(@"C:\apps\run.vbs")]
    [InlineData(@"C:\apps\run.js")]
    [InlineData(@"C:\apps\run.msi")]
    [InlineData(@"C:\apps\run.dll")]
    [InlineData(@"C:\apps\run.url")]
    [InlineData(@"C:\apps\.exe")]
    [InlineData(@"C:\apps\run.exe.txt")]
    public void Only_exe_files_are_accepted(string path) => Assert.Equal(ExecutableRefusal.NotExe, ExecutablePolicy.CheckPath(path));

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("PowerShell.exe")]
    [InlineData("pwsh.exe")]
    [InlineData("wscript.exe")]
    [InlineData("cscript.exe")]
    [InlineData("mshta.exe")]
    [InlineData("rundll32.exe")]
    [InlineData("regsvr32.exe")]
    [InlineData("msiexec.exe")]
    [InlineData("python.exe")]
    [InlineData("python3.12.exe")]
    [InlineData("pythonw.exe")]
    [InlineData("node.exe")]
    [InlineData("java.exe")]
    [InlineData("wsl.exe")]
    [InlineData("explorer.exe")]
    [InlineData("Update.exe")]
    [InlineData("setup.exe")]
    [InlineData("unins000.exe")]
    [InlineData("AppInstaller.exe")]
    [InlineData("Sysacta.exe")]
    public void Shells_interpreters_launchers_and_installers_are_denied(string name) =>
        Assert.Equal(ExecutableRefusal.DeniedProgram, ExecutablePolicy.CheckPath(@"C:\Tools\" + name));

    [Theory]
    [InlineData("PowerShell.EXE.MUI", true)]
    [InlineData("Cmd.Exe.MUI", true)]
    [InlineData("python.exe", true)]
    [InlineData("Sysacta.dll", true)]
    [InlineData("notepad++.exe", false)]
    [InlineData("vlc.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Version_resource_names_are_checked_by_their_stem(string? name, bool denied) => Assert.Equal(denied, ExecutablePolicy.IsDeniedName(name));

    [Fact]
    public void Packaged_app_folders_are_protected() =>
        Assert.Equal(ExecutableRefusal.ProtectedLocation, ExecutablePolicy.CheckPath(@"C:\Program Files\WindowsApps\Pkg_1.0\app.exe"));

    // ---- PE header ----------------------------------------------------------------------------------------------

    private static byte[] Pe(ushort machine = 0x8664, ushort characteristics = 0x0022, ushort magic = 0x20B, ushort subsystem = 2, int peOffset = 0x80)
    {
        var bytes = new byte[1024];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(peOffset).CopyTo(bytes, 0x3C);
        if (peOffset < 0 || peOffset + 94 > bytes.Length)
        {
            return bytes;
        }

        "PE\0\0"u8.CopyTo(bytes.AsSpan(peOffset));
        BitConverter.GetBytes(machine).CopyTo(bytes, peOffset + 4);
        BitConverter.GetBytes(characteristics).CopyTo(bytes, peOffset + 22);
        BitConverter.GetBytes(magic).CopyTo(bytes, peOffset + 24);
        BitConverter.GetBytes(subsystem).CopyTo(bytes, peOffset + 24 + 68);
        return bytes;
    }

    [Fact]
    public void Executable_images_for_supported_machines_and_subsystems_pass()
    {
        Assert.True(ExecutablePolicy.IsLaunchableImage(Pe()));
        Assert.True(ExecutablePolicy.IsLaunchableImage(Pe(machine: 0x014C, magic: 0x10B, subsystem: 3)));
        Assert.True(ExecutablePolicy.IsLaunchableImage(Pe(machine: 0xAA64)));
    }

    [Fact]
    public void Fake_dll_driver_and_truncated_images_are_refused()
    {
        Assert.False(ExecutablePolicy.IsLaunchableImage("#!/bin/sh\necho hi"u8));
        Assert.False(ExecutablePolicy.IsLaunchableImage(Encoding.ASCII.GetBytes("MZ this is not a pe file")));
        Assert.False(ExecutablePolicy.IsLaunchableImage(Pe(characteristics: 0x2022)));   // DLL
        Assert.False(ExecutablePolicy.IsLaunchableImage(Pe(characteristics: 0x1022)));   // system file (driver)
        Assert.False(ExecutablePolicy.IsLaunchableImage(Pe(characteristics: 0x0020)));   // not executable
        Assert.False(ExecutablePolicy.IsLaunchableImage(Pe(subsystem: 1)));              // native
        Assert.False(ExecutablePolicy.IsLaunchableImage(Pe(subsystem: 10)));             // EFI
        Assert.False(ExecutablePolicy.IsLaunchableImage(Pe(machine: 0x01C4)));           // ARM32
        Assert.False(ExecutablePolicy.IsLaunchableImage(Pe(peOffset: 0x3F0)));           // header beyond the bytes read
        Assert.False(ExecutablePolicy.IsLaunchableImage(Pe(peOffset: -8)));
        Assert.False(ExecutablePolicy.IsLaunchableImage(Pe().AsSpan(0, 0x90)));
    }

    // ---- command line -------------------------------------------------------------------------------------------

    /// <summary>The documented CommandLineToArgvW / MSVC rules for the arguments after the program name.</summary>
    private static List<string> Parse(string commandLine)
    {
        Assert.StartsWith("\"", commandLine);
        var rest = commandLine[(commandLine.IndexOf('"', 1) + 1)..];
        var args = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false, inArg = false;
        for (var i = 0; i < rest.Length; i++)
        {
            var c = rest[i];
            if (c == '\\')
            {
                var n = 0;
                while (i < rest.Length && rest[i] == '\\') { n++; i++; }
                if (i < rest.Length && rest[i] == '"')
                {
                    current.Append('\\', n / 2);
                    if (n % 2 == 1) current.Append('"'); else inQuotes = !inQuotes;
                }
                else
                {
                    current.Append('\\', n);
                    i--;
                }

                inArg = true;
            }
            else if (c == '"') { inQuotes = !inQuotes; inArg = true; }
            else if ((c == ' ' || c == '\t') && !inQuotes)
            {
                if (inArg) { args.Add(current.ToString()); current.Clear(); inArg = false; }
            }
            else { current.Append(c); inArg = true; }
        }

        if (inArg) args.Add(current.ToString());
        return args;
    }

#pragma warning disable CA1861 // test data, built once per run
    public static TheoryData<string[]> ArgumentLists => new()
    {
        Array.Empty<string>(),
        new[] { "--profile", "work" },
        new[] { "" },
        new[] { "", "", "x" },
        new[] { "with space", "tab\there" },
        new[] { "quote\"inside", "\"" },
        new[] { @"C:\path\", @"trailing\\", @"\\server\share\" },
        new[] { @"a\\""b", @"\""" },
        new[] { "日本語 の 引数", "emoji😀" },
        new[] { "&& calc", "| whoami", "%PATH%", "^", "<x>" },
    };
#pragma warning restore CA1861

    [Theory]
    [MemberData(nameof(ArgumentLists))]
    public void Command_line_round_trips_exactly(string[] args)
    {
        var line = ExecutablePolicy.BuildCommandLine(@"C:\Program Files\App\app.exe", args);
        Assert.StartsWith("\"C:\\Program Files\\App\\app.exe\"", line);
        Assert.Equal(args, Parse(line));
    }

    [Fact]
    public void Arguments_cannot_inject_another_argument_or_program()
    {
        var line = ExecutablePolicy.BuildCommandLine(@"C:\a\app.exe", ["x\" \"--evil", "y\\\" z"]);
        Assert.Equal(["x\" \"--evil", "y\\\" z"], Parse(line));
        Assert.Throws<ArgumentException>(() => ExecutablePolicy.BuildCommandLine("C:\\a\"\\app.exe", []));
    }

    // ---- start time ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(133_000_000_000_000_001L)]
    [InlineData(2_650_467_743_999_999_999L)]
    public void Start_time_is_lossless(long fileTime)
    {
        var text = ProcessStartTime.ToText(fileTime);
        Assert.Matches(ProcessStartTime.Pattern, text);
        Assert.True(ProcessStartTime.TryParse(text, out var parsed));
        Assert.Equal(fileTime, parsed);
    }

    [Theory]
    [InlineData("2026-11-28T10:25:01Z")]
    [InlineData("2026-11-28T10:25:01.123Z")]
    [InlineData("2026-11-28T10:25:01.1234567+09:00")]
    [InlineData("2026-11-28T10:25:01.1234567")]
    [InlineData("2026-11-28 10:25:01.1234567Z")]
    [InlineData("2026-13-28T10:25:01.1234567Z")]
    [InlineData("1600-12-31T23:59:59.9999999Z")]
    [InlineData("２０２６-11-28T10:25:01.1234567Z")]
    [InlineData("")]
    public void Start_time_accepts_only_the_exact_format(string text) => Assert.False(ProcessStartTime.TryParse(text, out _));

    // ---- allow-list limits --------------------------------------------------------------------------------------

    [Fact]
    public void Fixed_arguments_are_limited()
    {
        Assert.Null(AppRules.ValidateFixedArgs(["", "a b", "\"q\"", "日本語"]));
        Assert.NotNull(AppRules.ValidateFixedArgs(Enumerable.Repeat("a", 17).ToArray()));
        Assert.NotNull(AppRules.ValidateFixedArgs([new string('a', 257)]));
        Assert.NotNull(AppRules.ValidateFixedArgs(Enumerable.Repeat(new string('a', 200), 11).ToArray()));
        Assert.NotNull(AppRules.ValidateFixedArgs(["a\nb"]));
        Assert.NotNull(AppRules.ValidateFixedArgs(["a\0b"]));
        Assert.NotNull(AppRules.ValidateFixedArgs(["a\u202Eb"]));
        Assert.NotNull(AppRules.ValidateFixedArgs(["\uD800"]));
    }

    [Fact]
    public void Display_names_and_ids_are_validated()
    {
        Assert.Null(AppRules.ValidateDisplayName("メモ帳 2"));
        Assert.NotNull(AppRules.ValidateDisplayName(""));
        Assert.NotNull(AppRules.ValidateDisplayName("   "));
        Assert.NotNull(AppRules.ValidateDisplayName(@"C:\app"));
        Assert.NotNull(AppRules.ValidateDisplayName("a\u0007"));
        Assert.NotNull(AppRules.ValidateDisplayName(new string('a', 65)));
        var id = AppRules.NewAppId();
        Assert.True(AppRules.IsAppId(id));
        Assert.NotEqual(id, AppRules.NewAppId());
        Assert.False(AppRules.IsAppId(id.ToUpperInvariant()));
        Assert.False(AppRules.IsAppId(id + "\n"));
        Assert.False(AppRules.IsAppId("notepad"));
    }

    [Fact]
    public void Process_rules_refuse_every_unknown_or_protected_fact()
    {
        var ok = new ProcessFacts(10, 1, "a.exe", true, 0x2000, false, false, false, false, "app_x");
        Assert.Null(ProcessRules.Evaluate(ok));
        Assert.Null(ProcessRules.Evaluate(ok with { IntegrityRid = 0x2100 }));
        Assert.Equal(TerminateBlock.Self, ProcessRules.Evaluate(ok with { IsSelf = true }));
        Assert.Equal(TerminateBlock.SysactaParent, ProcessRules.Evaluate(ok with { IsParent = true }));
        Assert.Equal(TerminateBlock.OtherUser, ProcessRules.Evaluate(ok with { SameUser = false }));
        Assert.Equal(TerminateBlock.Unverified, ProcessRules.Evaluate(ok with { SameUser = null }));
        Assert.Equal(TerminateBlock.Unverified, ProcessRules.Evaluate(ok with { Protected = null }));
        Assert.Equal(TerminateBlock.Unverified, ProcessRules.Evaluate(ok with { Critical = null }));
        Assert.Equal(TerminateBlock.Unverified, ProcessRules.Evaluate(ok with { IntegrityRid = null }));
        Assert.Equal(TerminateBlock.Protected, ProcessRules.Evaluate(ok with { Protected = true }));
        Assert.Equal(TerminateBlock.Critical, ProcessRules.Evaluate(ok with { Critical = true }));
        Assert.Equal(TerminateBlock.Elevated, ProcessRules.Evaluate(ok with { IntegrityRid = 0x3000 }));
        Assert.Equal(TerminateBlock.Elevated, ProcessRules.Evaluate(ok with { IntegrityRid = 0x4000 }));
        Assert.Equal(TerminateBlock.LowIntegrity, ProcessRules.Evaluate(ok with { IntegrityRid = 0x1000 }));
        Assert.Equal(TerminateBlock.NotRegistered, ProcessRules.Evaluate(ok with { AppId = null }));
    }
}
