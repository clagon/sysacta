using Sysacta.Core.Auditing;
using Sysacta.Core.Security;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Display;
using Sysacta.Tests;

namespace Sysacta.Core.Tests;

public class DisplaySetLevelToolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Brightness = "display_set_brightness";
    private const string Contrast = "display_set_contrast";

    private static (Harness H, FakeDisplays Displays, Caller Caller) Setup(TimeSpan? timeout = null)
    {
        var displays = new FakeDisplays();
        var h = new Harness(store =>
        [
            timeout is null ? new DisplaySetBrightnessTool(displays, store) : new ShortTimeoutBrightness(displays, store, timeout.Value),
            new DisplaySetContrastTool(displays, store),
        ]);
        var (caller, _) = h.Store.AddClient("c1", Brightness, Contrast);
        return (h, displays, caller);
    }

    /// <summary>The production execution path with a short timeout.</summary>
    private sealed class ShortTimeoutBrightness(FakeDisplays displays, Core.Devices.IDeviceAliases aliases, TimeSpan timeout)
        : DisplaySetLevelTool<DisplaySetBrightnessArgs>(displays, aliases)
    {
        private protected override DisplayFeature Feature => DisplayFeature.Brightness;

        public override ToolDescriptor Descriptor { get; } =
            new DisplaySetBrightnessTool(displays, aliases).Descriptor with { Timeout = timeout };
    }

    private static Task<ToolResult> Set(Harness h, Caller caller, int level, string? device = null, string tool = Brightness, CancellationToken? ct = null) =>
        h.Executor.ExecuteAsync(caller, tool, device is null ? TestJson.Args(new { level }) : TestJson.Args(new { level, device }), ct ?? Ct);

    private static string[] Statuses(ToolResult r) => [.. r.Data!["devices"]!.AsArray().Select(d => (string)d!["status"]!)];

    private static async Task Drain(Harness h)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (h.Executor.InFlight > 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "Calls did not finish.");
            await Task.Delay(10, Ct);
        }
    }

    [Theory]
    [InlineData(Brightness)]
    [InlineData(Contrast)]
    public async Task A_change_is_written_once_and_verified(string tool)
    {
        var (h, displays, caller) = Setup();

        var r = await Set(h, caller, 30, tool: tool);

        Assert.True(r.IsSuccess);
        Assert.Equal(Verification.Verified, r.Verification);
        Assert.Equal(["changed"], Statuses(r));
        var d = r.Data!["devices"]![0]!;
        Assert.Equal(30, (int)d["current_level"]!);
        Assert.True((bool)d["changed"]!);
        var write = Assert.Single(displays.Writes);
        Assert.Equal(tool == Brightness ? DisplayFeature.Brightness : DisplayFeature.Contrast, write.Feature);
        Assert.Equal(0, displays.OpenControls);
        Assert.Equal(AuditOutcome.Pending, h.Audit.Events[0].Outcome);
        Assert.Equal(AuditOutcome.Success, h.Audit.Final(r.RequestId).Outcome);
        Assert.Equal("""{"level":30,"target":"display-a"}""", h.Audit.Final(r.RequestId).Arguments);
    }

    [Fact]
    public async Task Native_ranges_are_respected_and_the_effective_level_is_reported()
    {
        var (h, displays, caller) = Setup();
        displays.Levels[("display-a", DisplayFeature.Brightness)] = new FakeLevel { Current = 0, Allowed = [0, 25, 50, 75, 100] };

        var r = await Set(h, caller, 60);

        Assert.True(r.IsSuccess);
        Assert.Equal(60, (int)r.Data!["requested_level"]!);
        Assert.Equal(50, (int)r.Data["devices"]![0]!["effective_level"]!);
        Assert.Equal(50, Assert.Single(displays.Writes).Value);

        var same = await Set(h, caller, 55); // rounds to the current value
        Assert.Equal(["unchanged"], Statuses(same));
        Assert.Equal(Verification.Verified, same.Verification);
        Assert.False((bool)same.Data!["devices"]![0]!["changed"]!);
        Assert.Single(displays.Writes);
    }

    [Fact]
    public async Task A_late_value_is_re_read_without_writing_again()
    {
        var (h, displays, caller) = Setup();
        displays.Brightness("display-a").ApplyDelayReads = 2;

        var r = await Set(h, caller, 30);

        Assert.True(r.IsSuccess);
        Assert.Single(displays.Writes);
    }

    [Theory]
    [InlineData(40, true)] // the monitor quantized the value
    [InlineData(50, false)] // the monitor ignored it
    public async Task A_read_back_mismatch_is_verification_failed(long stored, bool changed)
    {
        var (h, displays, caller) = Setup();
        displays.Brightness("display-a").Apply = _ => stored;

        var r = await Set(h, caller, 30);

        Assert.Equal(ToolStatus.Error, r.Status);
        Assert.Equal(ErrorCodes.VerificationFailed, r.Error!.Code);
        Assert.False(r.Error.Retryable);
        Assert.Equal(["failed"], Statuses(r));
        Assert.Equal(changed, (bool)r.Data!["devices"]![0]!["changed"]!);
        Assert.Single(displays.Writes);
        Assert.Equal(AuditOutcome.Error, h.Audit.Final(r.RequestId).Outcome);
    }

    [Theory]
    [InlineData("write")]
    [InlineData("readback")]
    [InlineData("unexpected")]
    public async Task A_failure_after_the_write_started_is_unknown(string where)
    {
        var (h, displays, caller) = Setup();
        var level = displays.Brightness("display-a");
        var failure = new ToolFailureException(ErrorCodes.DeviceDisconnected, "fixed");
        if (where == "write")
        {
            level.WriteFailure = failure;
        }
        else if (where == "readback")
        {
            level.ReadFailureAfterWrite = failure;
        }
        else
        {
            level.WriteFailure = new InvalidOperationException(@"C:\secret");
        }

        var r = await Set(h, caller, 30, "all");

        Assert.Equal(ToolStatus.Unknown, r.Status);
        Assert.Equal(Verification.Unknown, r.Verification);
        Assert.Equal(["unknown", "not_attempted"], Statuses(r)); // nothing further is written
        Assert.Null(r.Data!["devices"]![0]!["changed"]);
        Assert.Single(displays.Writes);
        Assert.False(r.Error!.Retryable);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(r), StringComparison.Ordinal);
        Assert.Equal(AuditOutcome.Unknown, h.Audit.Final(r.RequestId).Outcome);
        Assert.Equal(0, displays.OpenControls);
    }

    [Theory]
    [InlineData("NOT_SUPPORTED")]
    [InlineData("OS_API_ERROR")]
    [InlineData("DEVICE_DISCONNECTED")]
    public async Task An_unreadable_current_value_means_no_write(string code)
    {
        var (h, displays, caller) = Setup();
        displays.Brightness("display-a").ReadFailure = new ToolFailureException(code, "fixed");

        var r = await Set(h, caller, 30);

        Assert.Equal(ToolStatus.Error, r.Status);
        Assert.Equal(code, r.Error!.Code);
        Assert.Equal(["failed"], Statuses(r));
        Assert.Empty(displays.Writes);
    }

    [Fact]
    public async Task Contrast_of_an_internal_panel_is_not_replaced_by_another_operation()
    {
        var (h, displays, caller) = Setup();
        displays.Add("display-p", "Panel", kind: DisplayKind.Internal);

        var r = await Set(h, caller, 30, "display-p", Contrast);

        Assert.Equal(ErrorCodes.NotSupported, r.Error!.Code);
        Assert.Empty(displays.Writes);
    }

    [Fact]
    public async Task All_changes_every_display_once_and_includes_unchanged_ones()
    {
        var (h, displays, caller) = Setup();
        displays.Add("display-c", "c");
        displays.Brightness("display-b").Current = 30;

        var r = await Set(h, caller, 30, "all");

        Assert.True(r.IsSuccess);
        Assert.Equal(["changed", "unchanged", "changed"], Statuses(r));
        Assert.Equal(["display-a", "display-c"], displays.Writes.Select(w => w.Id));
        Assert.Equal("""{"level":30,"target":"all"}""", h.Audit.Final(r.RequestId).Arguments);
    }

    [Fact]
    public async Task All_continues_past_known_failures_and_reports_partial()
    {
        var (h, displays, caller) = Setup();
        displays.Add("display-0", "mirror", cloned: true);
        displays.Brightness("display-a").ReadFailure = new ToolFailureException(ErrorCodes.NotSupported, "fixed");

        var r = await Set(h, caller, 30, "all");

        Assert.Equal(ToolStatus.Partial, r.Status);
        Assert.Equal(Verification.Verified, r.Verification);
        Assert.Equal(["failed", "failed", "changed"], Statuses(r));
        Assert.Equal(ErrorCodes.AmbiguousTarget, r.Error!.Code);
        Assert.Equal("display-b", Assert.Single(displays.Writes).Id);
        Assert.Equal(AuditOutcome.Partial, h.Audit.Final(r.RequestId).Outcome);
    }

    [Fact]
    public async Task All_with_nothing_changed_and_a_missed_target_is_an_error()
    {
        var (h, displays, caller) = Setup();
        displays.Brightness("display-a").Current = 30;
        displays.Brightness("display-b").ReadFailure = new ToolFailureException(ErrorCodes.NotSupported, "fixed");

        var r = await Set(h, caller, 30, "all");

        Assert.Equal(ToolStatus.Error, r.Status);
        Assert.Equal(ErrorCodes.NotSupported, r.Error!.Code);
        Assert.Equal(["unchanged", "failed"], Statuses(r));
        Assert.Empty(displays.Writes);
    }

    [Fact]
    public async Task All_never_reports_success_while_a_display_cannot_be_identified()
    {
        var (h, displays, caller) = Setup();
        displays.Unidentified = 1;

        var r = await Set(h, caller, 30, "all");

        Assert.Equal(ToolStatus.Partial, r.Status);
        Assert.Equal(1, (int)r.Data!["unidentified_displays"]!);
    }

    [Fact]
    public async Task All_is_refused_before_any_change_when_there_are_too_many_or_no_displays()
    {
        var (h, displays, caller) = Setup();
        for (var i = 0; i < DisplaySetBrightnessTool.MaxBatch - 1; i++)
        {
            displays.Add($"display-x{i}", "x");
        }

        var tooMany = await Set(h, caller, 30, "all");
        displays.Displays.Clear();
        var none = await Set(h, caller, 30, "all");

        Assert.Equal(ErrorCodes.InvalidArgument, tooMany.Error!.Code);
        Assert.Equal(ErrorCodes.TargetNotFound, none.Error!.Code);
        Assert.Empty(displays.Writes);
    }

    [Fact]
    public async Task All_writes_a_duplicated_id_only_once()
    {
        var (h, displays, caller) = Setup();
        displays.Displays.Add(displays.Displays[0]);

        var r = await Set(h, caller, 30, "all");

        Assert.Equal(2, r.Data!["devices"]!.AsArray().Count);
        Assert.Equal(2, displays.Writes.Count);
    }

    [Fact]
    public async Task Displays_connected_during_the_run_are_not_added()
    {
        var (h, displays, caller) = Setup();
        displays.OnWrite = _ =>
        {
            if (displays.Displays.Count == 2)
            {
                displays.Add("display-new", "new");
            }
        };

        var r = await Set(h, caller, 30, "all");

        Assert.True(r.IsSuccess);
        Assert.Equal(["display-a", "display-b"], displays.Writes.Select(w => w.Id));
    }

    [Fact]
    public async Task A_display_removed_during_the_run_is_reported_and_the_rest_continue()
    {
        var (h, displays, caller) = Setup();
        displays.Add("display-c", "c");
        displays.OnWrite = id =>
        {
            if (id == "display-a")
            {
                displays.Displays.RemoveAll(d => d.Id == "display-b");
            }
        };

        var r = await Set(h, caller, 30, "all");

        Assert.Equal(ToolStatus.Partial, r.Status);
        Assert.Equal(["changed", "failed", "changed"], Statuses(r));
        Assert.Equal(ErrorCodes.DeviceDisconnected, r.Error!.Code);
    }

    private static void ApplyChange(Harness h, string change)
    {
        switch (change)
        {
            case "revoke": h.Store.Revoke("c1"); break;
            case "reissue": h.Store.Reissue("c1"); break;
            case "grant": h.Store.Grants[("c1", Brightness)] = false; break;
            case "global": h.Store.EnabledTools[Brightness] = false; break;
            case "expire": h.Store.Clients["c1"] = h.Store.Clients["c1"] with { ExpiresAt = DateTimeOffset.UnixEpoch }; break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("reissue")]
    [InlineData("grant")]
    [InlineData("global")]
    [InlineData("expire")]
    public async Task Permission_change_before_the_first_write_means_zero_writes(string change)
    {
        var (h, displays, caller) = Setup();
        var applied = false;
        displays.OnRead = _ =>
        {
            if (!applied)
            {
                applied = true;
                h.Update(() => ApplyChange(h, change));
            }
        };

        var r = await Set(h, caller, 30, "all");

        Assert.Equal(ToolStatus.Error, r.Status);
        Assert.Contains(r.Error!.Code, new[] { ErrorCodes.PermissionDenied, ErrorCodes.ToolDisabled });
        Assert.Empty(displays.Writes);
        Assert.Equal(AuditOutcome.Denied, h.Audit.Final(r.RequestId).Outcome);
        Assert.Equal(0, displays.OpenControls);
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("reissue")]
    [InlineData("grant")]
    [InlineData("global")]
    [InlineData("expire")]
    public async Task Permission_change_during_the_run_stops_before_the_next_write(string change)
    {
        var (h, displays, caller) = Setup();
        displays.Add("display-c", "c");
        displays.OnWrite = _ => h.Update(() => ApplyChange(h, change));

        var r = await Set(h, caller, 30, "all");

        Assert.Equal(ToolStatus.Partial, r.Status);
        Assert.Equal(["changed", "not_attempted", "not_attempted"], Statuses(r));
        Assert.Contains(r.Error!.Code, new[] { ErrorCodes.PermissionDenied, ErrorCodes.ToolDisabled });
        Assert.Single(displays.Writes);
    }

    [Fact]
    public async Task An_unwritable_audit_log_means_zero_writes()
    {
        var (h, displays, caller) = Setup();
        h.Audit.Fail = true;

        var r = await Set(h, caller, 30, "all");

        Assert.Equal(ErrorCodes.AuditUnavailable, r.Error!.Code);
        Assert.Empty(displays.Writes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancel_or_timeout_during_a_write_reports_progress_and_stops(bool cancel)
    {
        var (h, displays, caller) = Setup(cancel ? null : TimeSpan.FromMilliseconds(500));
        displays.Add("display-c", "c");
        using var release = new ManualResetEventSlim();
        using var inWrite = new ManualResetEventSlim();
        displays.OnWrite = id =>
        {
            if (id == "display-b")
            {
                inWrite.Set();
                release.Wait(CancellationToken.None); // a native call that ignores cancellation
            }
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var call = Set(h, caller, 30, "all", ct: cts.Token);
        inWrite.Wait(Ct);
        if (cancel)
        {
            await cts.CancelAsync();
        }

        var r = await call;

        Assert.Equal(ToolStatus.Unknown, r.Status);
        Assert.Equal(cancel ? ErrorCodes.Cancelled : ErrorCodes.Timeout, r.Error!.Code);
        Assert.Equal(["changed", "unknown", "not_attempted"], Statuses(r));
        Assert.Equal(1, h.Executor.InFlight); // locks and the control stay held until the native call ends
        Assert.Equal(1, displays.OpenControls);
        using var blockedCts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        blockedCts.CancelAfter(200);
        var blocked = await Set(h, caller, 10, "display-b", ct: blockedCts.Token);
        Assert.Equal(ToolStatus.Error, blocked.Status); // still waiting for the monitor's lock: nothing started

        release.Set();
        await Drain(h);
        Assert.Equal(0, displays.OpenControls);
        Assert.Equal(["display-a", "display-b"], displays.Writes.Select(w => w.Id));
    }

    [Fact]
    public async Task A_timeout_during_the_first_read_changes_nothing()
    {
        var (h, displays, caller) = Setup(TimeSpan.FromMilliseconds(300));
        using var release = new ManualResetEventSlim();
        displays.OnRead = _ => release.Wait(CancellationToken.None);

        var r = await Set(h, caller, 30);

        Assert.Equal(ToolStatus.Error, r.Status);
        Assert.Equal(ErrorCodes.Timeout, r.Error!.Code);
        release.Set();
        await Drain(h);
        Assert.Empty(displays.Writes);
    }

    [Theory]
    [InlineData(Brightness, "display-a", true)]
    [InlineData(Contrast, "display-a", true)] // contrast shares the monitor's lock
    [InlineData(Contrast, "display-b", false)] // another monitor is independent
    [InlineData(Brightness, "all", true)]
    public async Task Changes_of_the_same_monitor_are_serialized(string second, string device, bool waits)
    {
        var (h, displays, caller) = Setup();
        h.Store.Aliases[("display", "desk")] = "display-a";
        using var release = new ManualResetEventSlim();
        using var inWrite = new ManualResetEventSlim();
        var first = true;
        displays.OnWrite = _ =>
        {
            if (first)
            {
                first = false;
                inWrite.Set();
                release.Wait(CancellationToken.None);
            }
        };

        var holder = Set(h, caller, 30, "desk"); // the alias resolves to the same lock as the id
        inWrite.Wait(Ct);
        var other = Set(h, caller, 40, device, second);
        var finishedWhileHeld = await Task.WhenAny(other, Task.Delay(waits ? 300 : 5000, Ct)) == other;

        Assert.Equal(!waits, finishedWhileHeld);
        release.Set();
        Assert.True((await holder).IsSuccess);
        Assert.True((await other).IsSuccess);
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("reissue")]
    [InlineData("grant")]
    [InlineData("global")]
    [InlineData("expire")]
    public async Task Permission_change_while_preparing_a_write_stops_that_write(string change)
    {
        foreach (var (during, expected) in new[]
        {
            ("display-a", new[] { "not_attempted", "not_attempted" }), // the first write
            ("display-b", new[] { "changed", "not_attempted" }), // a later write
        })
        {
            var (h, displays, caller) = Setup();
            displays.OnPrepare = id =>
            {
                if (id == during)
                {
                    h.Update(() => ApplyChange(h, change));
                }
            };

            var r = await Set(h, caller, 30, "all");

            Assert.Contains(r.Error!.Code, new[] { ErrorCodes.PermissionDenied, ErrorCodes.ToolDisabled });
            Assert.Equal(during == "display-a" ? 0 : 1, displays.Writes.Count);
            Assert.Equal(0, displays.OpenControls);
            if (during == "display-b")
            {
                Assert.Equal(ToolStatus.Partial, r.Status);
                Assert.Equal(expected, Statuses(r));
            }
            else
            {
                Assert.Equal(AuditOutcome.Denied, h.Audit.Final(r.RequestId).Outcome);
            }
        }
    }

    [Theory]
    [InlineData("display-a", false)]
    [InlineData("display-a", true)]
    [InlineData("display-b", false)]
    [InlineData("display-b", true)]
    public async Task A_timeout_or_cancel_while_preparing_a_write_never_writes_it(string during, bool cancel)
    {
        var (h, displays, caller) = Setup(cancel ? null : TimeSpan.FromMilliseconds(500));
        using var release = new ManualResetEventSlim();
        using var preparing = new ManualResetEventSlim();
        displays.OnPrepare = id =>
        {
            if (id == during)
            {
                preparing.Set();
                release.Wait(CancellationToken.None); // a slow lookup that ignores cancellation
            }
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var call = Set(h, caller, 30, "all", ct: cts.Token);
        preparing.Wait(Ct);
        if (cancel)
        {
            await cts.CancelAsync();
        }

        var r = await call;
        release.Set();
        await Drain(h);

        Assert.Equal(cancel ? ErrorCodes.Cancelled : ErrorCodes.Timeout, r.Error!.Code);
        Assert.Equal(0, displays.OpenControls);
        if (during == "display-a")
        {
            Assert.Equal(ToolStatus.Error, r.Status); // nothing had started
            Assert.Empty(displays.Writes);
        }
        else
        {
            Assert.Equal(ToolStatus.Unknown, r.Status);
            Assert.Equal(["changed", "not_attempted"], Statuses(r));
            Assert.Equal("display-a", Assert.Single(displays.Writes).Id);
        }
    }

    [Fact]
    public async Task A_failed_preparation_changes_nothing_and_the_batch_continues()
    {
        var (h, displays, caller) = Setup();
        displays.OnPrepare = id =>
        {
            if (id == "display-a")
            {
                throw new ToolFailureException(ErrorCodes.OsApiError, "fixed");
            }
        };

        var r = await Set(h, caller, 30, "all");

        Assert.Equal(ToolStatus.Partial, r.Status);
        Assert.Equal(["failed", "changed"], Statuses(r));
        Assert.Equal("display-b", Assert.Single(displays.Writes).Id);
    }

    [Fact]
    public async Task A_display_that_becomes_identifiable_while_waiting_still_counts_as_unidentified()
    {
        var (h, displays, caller) = Setup();
        displays.Unidentified = 1;
        h.Audit.OnWrite = e =>
        {
            if (e.Outcome == AuditOutcome.Pending) // after the batch was resolved, before it runs
            {
                displays.Unidentified = 0;
                displays.Add("display-late", "late");
            }
        };

        var r = await Set(h, caller, 30, "all");

        Assert.Equal(ToolStatus.Partial, r.Status);
        Assert.Equal(1, (int)r.Data!["unidentified_displays"]!);
        Assert.Equal(["display-a", "display-b"], displays.Writes.Select(w => w.Id));
    }

    [Fact]
    public async Task An_unidentified_display_connected_after_resolution_does_not_affect_the_batch()
    {
        var (h, displays, caller) = Setup();
        h.Audit.OnWrite = e =>
        {
            if (e.Outcome == AuditOutcome.Pending)
            {
                displays.Unidentified = 1;
            }
        };

        var r = await Set(h, caller, 30, "all");

        Assert.True(r.IsSuccess);
        Assert.Equal(0, (int)r.Data!["unidentified_displays"]!);
    }

    [Theory]
    [InlineData("prepare", null, "TARGET_CHANGED")]
    [InlineData("open", null, "TARGET_CHANGED")] // while waiting for the adapter
    [InlineData("read", null, "TARGET_CHANGED")]
    [InlineData("open", "display-a", null)] // an explicitly named display is still the target
    public async Task A_primary_change_after_opening_the_display_stops_a_default_targeted_write(string during, string? device, string? code)
    {
        var (h, displays, caller) = Setup();
        var moved = false;
        void MovePrimary(string _)
        {
            if (!moved)
            {
                moved = true;
                displays.Replace("display-a", d => d with { IsPrimary = false });
                displays.Replace("display-b", d => d with { IsPrimary = true });
            }
        }

        if (during == "open")
        {
            displays.OnOpen = MovePrimary;
        }
        else if (during == "read")
        {
            displays.OnRead = MovePrimary;
        }
        else
        {
            displays.OnPrepare = MovePrimary;
        }

        var r = await Set(h, caller, 30, device);

        Assert.Equal(code, r.Error?.Code);
        Assert.Equal(code is null ? 1 : 0, displays.Writes.Count);
        Assert.Equal(code is not null, r.Error?.Retryable ?? false);
        Assert.Equal(0, displays.OpenControls);
    }

    [Theory]
    [InlineData("primary", "TARGET_CHANGED")]
    [InlineData("disconnect", "DEVICE_DISCONNECTED")]
    public async Task A_topology_change_while_waiting_changes_nothing(string change, string code)
    {
        var (h, displays, caller) = Setup();
        using var release = new ManualResetEventSlim();
        using var inWrite = new ManualResetEventSlim();
        displays.OnWrite = _ =>
        {
            inWrite.Set();
            release.Wait(CancellationToken.None);
        };

        var holder = Set(h, caller, 30);
        inWrite.Wait(Ct);
        var waiting = Set(h, caller, 40, change == "primary" ? null : "display-a");
        await Task.Delay(200, Ct);
        if (change == "primary")
        {
            displays.Replace("display-a", d => d with { IsPrimary = false });
            displays.Replace("display-b", d => d with { IsPrimary = true });
        }
        else
        {
            displays.Displays.RemoveAll(d => d.Id == "display-a");
        }

        release.Set();
        await holder;
        var r = await waiting;

        Assert.Equal(code, r.Error!.Code);
        Assert.Equal("display-a", Assert.Single(displays.Writes).Id);
    }
}
