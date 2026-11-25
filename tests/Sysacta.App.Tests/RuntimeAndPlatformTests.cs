using System.IO;
using System.Net;
using Sysacta.App.ViewModels;

namespace Sysacta.App.Tests;

public class RuntimeAndPlatformTests
{
    private static int FreePort()
    {
        using var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    /// <summary>SEC-04: a save racing with a start must never leave the server on the old settings.</summary>
    [Fact]
    public async Task Concurrent_start_and_settings_save_end_consistent()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires Windows.");
        var directory = Directory.CreateTempSubdirectory("sysacta-runtime-").FullName;
        var runtime = new SysactaRuntime(directory);
        try
        {
            for (var i = 0; i < 5; i++)
            {
                await runtime.StopServerAsync();
                var port = FreePort();
                var start = Task.Run(runtime.StartServerAsync);
                var save = Task.Run(() => runtime.SaveSettingsAsync(s => s with { Port = port }, restartIfRunning: true));
                Assert.Null(await start);
                Assert.Null(await save);

                Assert.Equal(port, runtime.Settings.Port);
                Assert.Equal(port, runtime.Host.BoundPort);
                Assert.Equal(port, runtime.SettingsStore.Load().Settings.Port);
            }
        }
        finally
        {
            await runtime.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>SEC-07: Explorer is started by absolute path, with the folder as its only argument.</summary>
    [Fact]
    public void Data_folder_is_opened_with_an_absolute_explorer_path()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires Windows.");
        var info = GeneralViewModel.OpenFolderStartInfo(@"C:\Data\Sysacta");

        Assert.True(Path.IsPathFullyQualified(info.FileName));
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), info.FileName, ignoreCase: true);
        Assert.Equal(Environment.SystemDirectory, info.WorkingDirectory);
        Assert.False(info.UseShellExecute);
        Assert.Equal([@"C:\Data\Sysacta"], info.ArgumentList);
    }

    /// <summary>Audit log date filter: the end boundary uses the offset of the next midnight (DST end day).</summary>
    [Fact]
    public void End_date_boundary_uses_the_offset_of_the_next_midnight()
    {
        var eastern = TimeZoneInfo.CreateCustomTimeZone("Test Eastern", TimeSpan.FromHours(-5), "Test Eastern", "EST", "EDT",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                new DateTime(2000, 1, 1), new DateTime(2099, 12, 31), TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday)),
        ]);

        // 2026-11-01 is the first Sunday of November: EDT (-4) at its midnight, EST (-5) at the next midnight.
        var end = LogsViewModel.DayStart(new DateTime(2026, 11, 1), 1, eastern)!.Value;
        Assert.Equal(new DateTime(2026, 11, 2, 5, 0, 0, DateTimeKind.Utc), end.UtcDateTime);

        var start = LogsViewModel.DayStart(new DateTime(2026, 11, 1), 0, eastern)!.Value;
        Assert.Equal(new DateTime(2026, 11, 1, 4, 0, 0, DateTimeKind.Utc), start.UtcDateTime);
    }

    /// <summary>SEC-06: an intent left by a crashed process is closed as "unknown" when the next process starts.</summary>
    [Fact]
    public async Task Startup_closes_intents_left_by_a_previous_process()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires Windows.");
        var directory = Directory.CreateTempSubdirectory("sysacta-runtime-").FullName;
        var first = new SysactaRuntime(directory);
        first.Audit.Write(new Core.Auditing.AuditEvent
        {
            RequestId = "left", Timestamp = DateTimeOffset.UtcNow, Source = "mcp", ToolName = "audio_set_volume", Outcome = Core.Auditing.AuditOutcome.Pending,
        });
        await first.DisposeAsync();

        var second = new SysactaRuntime(directory);
        try
        {
            var e = Assert.Single(second.Audit.Query(new Infrastructure.AuditQuery()));
            Assert.Equal(Core.Auditing.AuditOutcome.Unknown, e.Outcome);
            Assert.Equal(Infrastructure.AuditLog.InterruptedCode, e.ErrorCode);
        }
        finally
        {
            await second.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Storage_summary_shows_limits_evictions_and_losses()
    {
        var limits = new Infrastructure.AuditLimits();
        var quiet = LogsViewModel.FormatStorage(new Infrastructure.AuditStatus(10, 2048, 0, null, 0, 0, 0, 0, limits));
        Assert.Contains("上限による削除はありません", quiet, StringComparison.Ordinal);
        Assert.Contains("集約", quiet, StringComparison.Ordinal);

        var busy = LogsViewModel.FormatStorage(new Infrastructure.AuditStatus(10, 2048, 5, DateTimeOffset.UtcNow, 2, 0, 0, 0, limits));
        Assert.Contains("古い記録", busy, StringComparison.Ordinal);
        Assert.Contains("記録できませんでした", busy, StringComparison.Ordinal);
    }
}
