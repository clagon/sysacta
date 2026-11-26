using Sysacta.Core.Auditing;
using Sysacta.Core.Security;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Display;
using Sysacta.Tests;

namespace Sysacta.Core.Tests;

public class DisplayModeToolTests
{
    private const string SetMode = "display_set_mode";
    private const string Restore = "display_restore_mode";
    private const string Secondary = "display-b";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (Harness H, FakeDisplays Displays, DisplayModeRecovery Recovery, Caller Caller) Setup()
    {
        var displays = new FakeDisplays();
        var recovery = new DisplayModeRecovery();
        var h = new Harness(store =>
        [
            new DisplaySetModeTool(displays, store, recovery),
            new DisplayRestoreModeTool(displays, recovery),
            new DisplaySetBrightnessTool(displays, store),
            new DisplaySetPowerTool(displays, store),
            new DisplaySetInputTool(displays, store),
        ]);
        var (caller, _) = h.Store.AddClient("c1", SetMode, "display_set_brightness", "display_set_power", "display_set_input");
        return (h, displays, recovery, caller);
    }

    private static string Id(string display, DisplayMode mode) => DisplayListModesTool.ModeId(display, mode);

    private static Task<ToolResult> Set(Harness h, Caller caller, DisplayMode mode, string device = Secondary, CancellationToken? ct = null) =>
        h.Executor.ExecuteAsync(caller, SetMode, TestJson.Args(new { device, mode_id = Id(device, mode) }), ct ?? Ct);

    private static Task<ToolResult> RestoreLocally(Harness h) => h.Executor.ExecuteAsync(Caller.LocalConsole, Restore, null, Ct);

    [Fact]
    public async Task A_mode_is_tested_applied_once_verified_and_recorded_for_recovery()
    {
        var (h, displays, recovery, caller) = Setup();

        var r = await Set(h, caller, FakeDisplays.Mode720p60);

        Assert.True(r.IsSuccess, r.Error?.Message);
        Assert.Equal(Verification.Verified, r.Verification);
        Assert.True((bool)r.Data!["changed"]!);
        Assert.True((bool)r.Data["recovery_available"]!);
        Assert.False((bool)r.Data["other_displays_changed"]!);
        Assert.Equal(1280, (int)r.Data["current_mode"]!["width"]!);
        Assert.Equal(1920, (int)r.Data["previous_mode"]!["width"]!);
        Assert.Equal([(Secondary, FakeDisplays.Mode720p60)], displays.ModeTests);
        Assert.Equal([(Secondary, FakeDisplays.Mode720p60)], displays.ModeWrites);
        Assert.Equal(new DisplayModeRecord(Secondary, "DELL P2314H", FakeDisplays.Mode1080p60, FakeDisplays.Mode720p60), recovery.Latest);
        Assert.Equal(0, displays.OpenControls);
        // Only the resolved target is audited; the client's mode_id text is not.
        Assert.Equal("""{"target":"display-b"}""", h.Audit.Final(r.RequestId).Arguments);
        Assert.Equal(AuditOutcome.Pending, h.Audit.Events[0].Outcome);
    }

    [Fact]
    public async Task The_current_mode_is_a_verified_no_op_without_test_or_write()
    {
        var (h, displays, recovery, caller) = Setup();

        var r = await Set(h, caller, FakeDisplays.Mode1080p60);

        Assert.True(r.IsSuccess);
        Assert.False((bool)r.Data!["changed"]!);
        Assert.False((bool)r.Data["recovery_available"]!);
        Assert.Empty(displays.ModeTests);
        Assert.Empty(displays.ModeWrites);
        Assert.Null(recovery.Latest);
    }

    [Theory]
    [InlineData("primary", ErrorCodes.NotSupported)]
    [InlineData("cloned", ErrorCodes.AmbiguousTarget)]
    [InlineData("virtual", ErrorCodes.NotSupported)]
    [InlineData("unknown", ErrorCodes.NotSupported)]
    public async Task Displays_without_a_safe_recovery_or_a_unique_monitor_are_refused(string kind, string code)
    {
        var (h, displays, recovery, caller) = Setup();
        var device = Secondary;
        switch (kind)
        {
            case "primary": device = "display-a"; break;
            case "cloned": displays.Replace(Secondary, d => d with { IsCloned = true }); break;
            case "virtual": displays.Replace(Secondary, d => d with { Kind = DisplayKind.Virtual }); break;
            case "unknown": displays.Replace(Secondary, d => d with { Kind = DisplayKind.Unknown }); break;
        }

        var r = await Set(h, caller, FakeDisplays.Mode720p60, device);

        Assert.Equal(code, r.Error?.Code);
        Assert.Empty(displays.ModeTests);
        Assert.Empty(displays.ModeWrites);
        Assert.Null(recovery.Latest);
    }

    [Fact]
    public async Task A_display_that_becomes_primary_while_waiting_is_refused()
    {
        var (h, displays, _, caller) = Setup();
        displays.OnOpen = _ =>
        {
            displays.Replace("display-a", d => d with { IsPrimary = false });
            displays.Replace(Secondary, d => d with { IsPrimary = true });
        };

        var r = await Set(h, caller, FakeDisplays.Mode720p60);

        Assert.Equal(ErrorCodes.NotSupported, r.Error?.Code);
        Assert.Empty(displays.ModeWrites);
    }

    [Theory]
    [InlineData("""{"mode_id":"mode-0"}""")]
    [InlineData("""{"device":null,"mode_id":"mode-0"}""")]
    [InlineData("""{"device":"all","mode_id":"mode-0"}""")]
    [InlineData("""{"device":"default","mode_id":"mode-0"}""")]
    [InlineData("""{"device":"display-b"}""")]
    [InlineData("""{"device":"display-b","mode_id":"mode-0","width":1280}""")]
    [InlineData("""{"device":"display-b","mode_id":"mode-\u0007"}""")]
    [InlineData("""{"device":"nope","mode_id":"mode-0"}""")]
    public async Task Missing_reserved_unknown_or_extra_arguments_are_refused(string json)
    {
        var (h, displays, _, caller) = Setup();

        var r = await h.Executor.ExecuteAsync(caller, SetMode, TestJson.Raw(json), Ct);

        Assert.False(r.IsSuccess);
        Assert.Contains(r.Error!.Code, new[] { ErrorCodes.InvalidArgument, ErrorCodes.TargetNotFound });
        Assert.Empty(displays.ModeWrites);
    }

    [Fact]
    public async Task Mode_ids_of_another_display_or_not_enumerated_now_are_refused()
    {
        var (h, displays, _, caller) = Setup();
        var foreign = Id("display-a", FakeDisplays.Mode720p60);
        var vanished = Id(Secondary, new DisplayMode(800, 600, 60, 32, false, 0, 0));

        foreach (var modeId in new[] { foreign, vanished })
        {
            var r = await h.Executor.ExecuteAsync(caller, SetMode, TestJson.Args(new { device = Secondary, mode_id = modeId }), Ct);
            Assert.Equal(ErrorCodes.InvalidArgument, r.Error?.Code);
        }

        Assert.Empty(displays.ModeTests);
        Assert.Empty(displays.ModeWrites);
    }

    [Fact]
    public async Task Rotation_and_scaling_changes_are_refused()
    {
        var (h, displays, _, caller) = Setup();
        var rotated = FakeDisplays.Mode720p60 with { Orientation = 90 };
        var stretched = FakeDisplays.Mode720p60 with { FixedOutput = 1 };
        displays.Modes[Secondary] = new([FakeDisplays.Mode1080p60, rotated, stretched], FakeDisplays.Mode1080p60);

        Assert.Equal(ErrorCodes.InvalidArgument, (await Set(h, caller, rotated)).Error?.Code);
        Assert.Equal(ErrorCodes.InvalidArgument, (await Set(h, caller, stretched)).Error?.Code);
        Assert.Empty(displays.ModeWrites);
    }

    [Fact]
    public async Task Without_a_readable_current_mode_there_is_no_recovery_and_nothing_changes()
    {
        var (h, displays, recovery, caller) = Setup();
        displays.Modes[Secondary] = displays.Modes[Secondary] with { Current = null };

        var r = await Set(h, caller, FakeDisplays.Mode720p60);

        Assert.Equal(ErrorCodes.OsApiError, r.Error?.Code);
        Assert.Empty(displays.ModeTests);
        Assert.Null(recovery.Latest);
    }

    [Fact]
    public async Task A_failed_windows_test_changes_nothing()
    {
        var (h, displays, recovery, caller) = Setup();
        displays.ModeTestFailure = new ToolFailureException(ErrorCodes.NotSupported, "fixed");

        var r = await Set(h, caller, FakeDisplays.Mode720p60);

        Assert.Equal(ErrorCodes.NotSupported, r.Error?.Code);
        Assert.Null(r.Data);
        Assert.Empty(displays.ModeWrites);
        Assert.Null(recovery.Latest);
    }

    [Fact]
    public async Task A_refused_change_that_reads_back_unchanged_is_an_error_and_keeps_the_earlier_record()
    {
        var (h, displays, recovery, caller) = Setup();
        displays.Add("display-c", "Third");
        Assert.True((await Set(h, caller, FakeDisplays.Mode720p60, "display-c")).IsSuccess);
        var earlier = recovery.Latest;
        displays.ApplyMode = (_, _) => null;
        displays.ModeApplyFailure = new ToolFailureException(ErrorCodes.OsApiError, "fixed");

        var r = await Set(h, caller, FakeDisplays.Mode720p60);

        Assert.Equal(ToolStatus.Error, r.Status);
        Assert.Equal(ErrorCodes.OsApiError, r.Error?.Code);
        Assert.False((bool)r.Data!["changed"]!);
        Assert.False((bool)r.Data["recovery_available"]!);
        Assert.Same(earlier, recovery.Latest);
        Assert.Equal(2, displays.ModeWrites.Count); // never retried
    }

    [Fact]
    public async Task A_reported_failure_whose_mode_took_effect_is_judged_by_the_read_back()
    {
        var (h, displays, recovery, caller) = Setup();
        displays.ModeApplyFailure = new ToolFailureException(ErrorCodes.OsApiError, "fixed");

        var r = await Set(h, caller, FakeDisplays.Mode720p60);

        Assert.True(r.IsSuccess);
        Assert.Equal(Verification.Verified, r.Verification);
        Assert.True((bool)r.Data!["recovery_available"]!);
        Assert.NotNull(recovery.Latest);
        Assert.Single(displays.ModeWrites);
    }

    [Fact]
    public async Task A_different_resulting_mode_fails_verification_and_stays_restorable()
    {
        var (h, displays, recovery, caller) = Setup();
        displays.ApplyMode = (_, _) => FakeDisplays.Mode1080p144;

        var r = await Set(h, caller, FakeDisplays.Mode720p60);

        Assert.Equal(ToolStatus.Error, r.Status);
        Assert.Equal(ErrorCodes.VerificationFailed, r.Error?.Code);
        Assert.True((bool)r.Data!["changed"]!);
        Assert.True((bool)r.Data["recovery_available"]!);
        Assert.NotNull(recovery.Latest);
    }

    [Fact]
    public async Task An_unreadable_result_is_unknown_and_stays_restorable()
    {
        var (h, displays, recovery, caller) = Setup();
        displays.ModeReadFailureAfterApply = new ToolFailureException(ErrorCodes.DeviceDisconnected, "fixed");

        var r = await Set(h, caller, FakeDisplays.Mode720p60);

        Assert.Equal(ToolStatus.Unknown, r.Status);
        Assert.Equal(Verification.Unknown, r.Verification);
        Assert.Null(r.Data!["changed"]);
        Assert.True((bool)r.Data["recovery_available"]!);
        Assert.NotNull(recovery.Latest);
        Assert.Equal(AuditOutcome.Unknown, h.Audit.Final(r.RequestId).Outcome);
    }

    [Fact]
    public async Task Changes_to_other_displays_are_reported_and_nothing_else_is_changed()
    {
        var (h, displays, _, caller) = Setup();
        displays.OnModeApply = _ => displays.Positions["display-a"] = (0, 100);

        var r = await Set(h, caller, FakeDisplays.Mode720p60);

        Assert.Equal(ToolStatus.Partial, r.Status);
        Assert.Equal(ErrorCodes.VerificationFailed, r.Error?.Code);
        Assert.True((bool)r.Data!["other_displays_changed"]!);
        Assert.Single(displays.ModeWrites);
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("disable")]
    public async Task Losing_authorization_while_preparing_writes_nothing_and_leaves_no_record(string change)
    {
        var (h, displays, recovery, caller) = Setup();
        displays.OnPrepare = _ => h.Update(() =>
        {
            if (change == "revoke")
            {
                h.Store.Revoke("c1");
            }
            else
            {
                h.Store.EnabledTools[SetMode] = false;
            }
        });

        var r = await Set(h, caller, FakeDisplays.Mode720p60);

        Assert.Equal(ToolStatus.Error, r.Status);
        Assert.Contains(r.Error!.Code, new[] { ErrorCodes.PermissionDenied, ErrorCodes.ToolDisabled });
        Assert.Empty(displays.ModeWrites);
        Assert.Null(recovery.Latest);
    }

    [Fact]
    public async Task A_hanging_mode_change_is_unknown_keeps_the_record_and_holds_the_display()
    {
        var (h, displays, recovery, caller) = Setup();
        using var release = new SemaphoreSlim(0);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        displays.OnModeApply = _ =>
        {
            cts.Cancel();
            release.Wait(TimeSpan.FromSeconds(10));
        };

        var r = await Set(h, caller, FakeDisplays.Mode720p60, ct: cts.Token);

        Assert.Equal(ToolStatus.Unknown, r.Status);
        Assert.True((bool)r.Data!["recovery_available"]!);
        Assert.NotNull(recovery.Latest);

        // The display stays locked: brightness of the same display waits for the native call.
        displays.OnModeApply = null;
        var brightness = h.Executor.ExecuteAsync(caller, "display_set_brightness", TestJson.Args(new { level = 10, device = Secondary }), Ct);
        await Task.Delay(100, Ct);
        Assert.False(brightness.IsCompleted);
        Assert.Empty(displays.Writes);
        release.Release();
        Assert.True((await brightness).IsSuccess);
    }

    [Fact]
    public async Task Restore_applies_the_recorded_original_once_and_clears_the_record()
    {
        var (h, displays, recovery, caller) = Setup();
        await Set(h, caller, FakeDisplays.Mode720p60);

        var r = await RestoreLocally(h);

        Assert.True(r.IsSuccess, r.Error?.Message);
        Assert.Equal(FakeDisplays.Mode1080p60, displays.CurrentMode(Secondary));
        Assert.Equal(Id(Secondary, FakeDisplays.Mode1080p60), (string)r.Data!["requested_mode_id"]!);
        Assert.False((bool)r.Data["recovery_available"]!);
        Assert.Null(recovery.Latest);
        Assert.Equal(2, displays.ModeTests.Count);
        var final = h.Audit.Final(r.RequestId);
        Assert.Equal("local_console", final.Source);
        Assert.Equal(Restore, final.ToolName);
        Assert.Equal("""{"target":"display-b"}""", final.Arguments);
    }

    [Fact]
    public async Task Restore_needs_no_permission_and_works_while_set_mode_is_disabled()
    {
        var (h, displays, _, caller) = Setup();
        await Set(h, caller, FakeDisplays.Mode720p60);
        h.Store.EnabledTools[SetMode] = false;

        Assert.True((await RestoreLocally(h)).IsSuccess);
        Assert.Equal(FakeDisplays.Mode1080p60, displays.CurrentMode(Secondary));
    }

    [Fact]
    public async Task Restore_is_not_a_tool_for_mcp_clients()
    {
        var (h, displays, recovery, caller) = Setup();
        await Set(h, caller, FakeDisplays.Mode720p60);
        h.Store.EnabledTools[Restore] = true;
        h.Store.Grants[("c1", Restore)] = true;

        var r = await h.Executor.ExecuteAsync(caller, Restore, null, Ct);

        Assert.Equal(ToolExecutor.UnknownToolCode, r.Error?.Code);
        Assert.DoesNotContain(h.Executor.ListTools(caller), t => t.Name == Restore);
        Assert.DoesNotContain(h.Executor.Registry.All, t => t.Name == Restore);
        Assert.Single(displays.ModeWrites);
        Assert.NotNull(recovery.Latest);
    }

    [Fact]
    public async Task Restore_without_a_record_does_nothing()
    {
        var (h, displays, _, _) = Setup();

        var r = await RestoreLocally(h);

        Assert.Equal(ErrorCodes.TargetNotFound, r.Error?.Code);
        Assert.Empty(displays.ModeWrites);
    }

    [Fact]
    public async Task A_record_made_stale_by_a_later_change_is_refused_and_dropped()
    {
        var (h, displays, recovery, caller) = Setup();
        await Set(h, caller, FakeDisplays.Mode720p60);
        displays.Modes[Secondary] = displays.Modes[Secondary] with { Current = FakeDisplays.Mode1080p144 }; // changed outside Sysacta

        var r = await RestoreLocally(h);

        Assert.Equal(ErrorCodes.TargetChanged, r.Error?.Code);
        Assert.Single(displays.ModeWrites);
        Assert.Null(recovery.Latest);
    }

    [Fact]
    public async Task A_replaced_or_disconnected_display_is_never_restored()
    {
        var (h, displays, recovery, caller) = Setup();
        await Set(h, caller, FakeDisplays.Mode720p60);
        displays.Displays.RemoveAll(d => d.Id == Secondary);
        displays.Add("display-other", "Another monitor on the same port");

        var r = await RestoreLocally(h);

        Assert.Equal(ErrorCodes.DeviceDisconnected, r.Error?.Code);
        Assert.Single(displays.ModeWrites);
        Assert.NotNull(recovery.Latest);
    }

    [Fact]
    public async Task Restoring_a_display_already_back_in_its_original_mode_is_a_no_op()
    {
        var (h, displays, recovery, caller) = Setup();
        await Set(h, caller, FakeDisplays.Mode720p60);
        displays.Modes[Secondary] = displays.Modes[Secondary] with { Current = FakeDisplays.Mode1080p60 };

        var r = await RestoreLocally(h);

        Assert.True(r.IsSuccess);
        Assert.False((bool)r.Data!["changed"]!);
        Assert.Single(displays.ModeWrites);
        Assert.Null(recovery.Latest);
    }

    [Fact]
    public async Task Restore_refuses_an_original_mode_windows_no_longer_offers()
    {
        var (h, displays, recovery, caller) = Setup();
        await Set(h, caller, FakeDisplays.Mode720p60);
        displays.Modes[Secondary] = new([FakeDisplays.Mode720p60], FakeDisplays.Mode720p60);

        var r = await RestoreLocally(h);

        Assert.Equal(ErrorCodes.NotSupported, r.Error?.Code);
        Assert.Single(displays.ModeWrites);
        Assert.NotNull(recovery.Latest);
    }

    [Fact]
    public async Task Brightness_of_the_same_display_waits_for_a_running_mode_change()
    {
        // Same lock key display:device:{id}. Other displays are serialized by the adapter's single gate (Windows only).
        var (h, displays, _, caller) = Setup();
        using var entered = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        displays.OnModeApply = _ =>
        {
            entered.Release();
            release.Wait(TimeSpan.FromSeconds(10));
        };

        var mode = Set(h, caller, FakeDisplays.Mode720p60);
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        var sameDisplay = h.Executor.ExecuteAsync(caller, "display_set_brightness", TestJson.Args(new { level = 10, device = Secondary }), Ct);
        await Task.Delay(100, Ct);

        Assert.False(sameDisplay.IsCompleted);
        release.Release();
        Assert.True((await mode).IsSuccess);
        Assert.True((await sameDisplay).IsSuccess);
    }

    [Fact]
    public async Task Power_and_input_of_the_same_display_wait_for_a_running_mode_change()
    {
        var (h, displays, _, caller) = Setup();
        using var entered = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        displays.OnModeApply = _ =>
        {
            entered.Release();
            release.Wait(TimeSpan.FromSeconds(10));
        };

        var mode = Set(h, caller, FakeDisplays.Mode720p60);
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        var power = h.Executor.ExecuteAsync(caller, "display_set_power", TestJson.Args(new { device = Secondary, state = "off" }), Ct);
        var input = h.Executor.ExecuteAsync(caller, "display_set_input", TestJson.Args(new { device = Secondary, input_id = Mccs.InputId(Secondary, 0x11) }), Ct);
        await Task.Delay(100, Ct);

        Assert.False(power.IsCompleted);
        Assert.False(input.IsCompleted);
        Assert.Empty(displays.SwitchWrites);
        release.Release();
        Assert.True((await mode).IsSuccess);
        Assert.True((await power).IsSuccess);
        Assert.True((await input).IsSuccess);
    }

    [Fact]
    public async Task A_record_replaced_while_the_restore_waits_for_the_adapter_is_not_applied()
    {
        var (h, displays, recovery, caller) = Setup();
        displays.Add("display-c", "Third");
        await Set(h, caller, FakeDisplays.Mode720p60);
        displays.OnOpen = id =>
        {
            if (id == Secondary)
            {
                displays.OnOpen = null;
                // Another display's change (different lock key) completes while the restore waits.
                Assert.True(Set(h, caller, FakeDisplays.Mode720p60, "display-c").GetAwaiter().GetResult().IsSuccess);
            }
        };

        var r = await RestoreLocally(h);

        Assert.Equal(ErrorCodes.TargetChanged, r.Error?.Code);
        Assert.Equal(FakeDisplays.Mode720p60, displays.CurrentMode(Secondary));
        Assert.Equal("display-c", recovery.Latest?.DisplayId);
    }

    [Fact]
    public async Task An_unexpected_resulting_mode_can_be_restored_right_away()
    {
        var (h, displays, recovery, caller) = Setup();
        displays.ApplyMode = (_, m) => m == FakeDisplays.Mode720p60 ? FakeDisplays.Mode1080p144 : m;

        var set = await Set(h, caller, FakeDisplays.Mode720p60);

        Assert.Equal(ErrorCodes.VerificationFailed, set.Error?.Code);
        Assert.True((bool)set.Data!["recovery_available"]!);
        Assert.Equal(FakeDisplays.Mode1080p144, recovery.Latest?.Applied);
        var restored = await RestoreLocally(h);
        Assert.True(restored.IsSuccess, restored.Error?.Code);
        Assert.Equal(FakeDisplays.Mode1080p60, displays.CurrentMode(Secondary));
        Assert.Null(recovery.Latest);
    }

    [Fact]
    public async Task An_unexpected_resulting_mode_changed_again_outside_sysacta_is_not_restored()
    {
        var (h, displays, recovery, caller) = Setup();
        displays.ApplyMode = (_, m) => m == FakeDisplays.Mode720p60 ? FakeDisplays.Mode1080p144 : m;
        await Set(h, caller, FakeDisplays.Mode720p60);
        displays.Modes[Secondary] = displays.Modes[Secondary] with { Current = FakeDisplays.Mode720p60 };

        var r = await RestoreLocally(h);

        Assert.Equal(ErrorCodes.TargetChanged, r.Error?.Code);
        Assert.Single(displays.ModeWrites);
        Assert.Null(recovery.Latest);
    }

    [Fact]
    public async Task A_restore_ending_in_another_mode_stays_restorable_from_that_mode()
    {
        var (h, displays, recovery, caller) = Setup();
        await Set(h, caller, FakeDisplays.Mode720p60);
        displays.ApplyMode = (_, m) => m == FakeDisplays.Mode1080p60 ? FakeDisplays.Mode1080p144 : m;

        var first = await RestoreLocally(h);

        Assert.Equal(ErrorCodes.VerificationFailed, first.Error?.Code);
        Assert.True((bool)first.Data!["recovery_available"]!);
        Assert.Equal(new DisplayModeRecord(Secondary, "DELL P2314H", FakeDisplays.Mode1080p60, FakeDisplays.Mode1080p144), recovery.Latest);
        displays.ApplyMode = (_, m) => m;
        Assert.True((await RestoreLocally(h)).IsSuccess);
        Assert.Equal(FakeDisplays.Mode1080p60, displays.CurrentMode(Secondary));
    }

    [Theory]
    [InlineData("test", "primary")]
    [InlineData("prepare", "primary")]
    [InlineData("test", "cloned")]
    [InlineData("prepare", "source")]
    [InlineData("prepare", "mode")]
    [InlineData("test", "gone")]
    public async Task A_change_during_the_preparation_writes_nothing(string when, string change)
    {
        var (h, displays, recovery, caller) = Setup();
        void Change(string _)
        {
            switch (change)
            {
                case "primary":
                    displays.Replace("display-a", d => d with { IsPrimary = false });
                    displays.Replace(Secondary, d => d with { IsPrimary = true });
                    break;
                case "cloned": displays.Replace(Secondary, d => d with { IsCloned = true }); break;
                case "source": displays.ReassignedSources.Add(Secondary); break;
                case "mode": displays.Modes[Secondary] = displays.Modes[Secondary] with { Current = FakeDisplays.Mode1080p144 }; break;
                case "gone": displays.Displays.RemoveAll(d => d.Id == Secondary); break;
            }
        }

        if (when == "test")
        {
            displays.OnModeTest = Change;
        }
        else
        {
            displays.OnPrepare = Change;
        }

        var r = await Set(h, caller, FakeDisplays.Mode720p60);

        Assert.Equal(ToolStatus.Error, r.Status);
        Assert.Equal(ErrorCodes.TargetChanged, r.Error?.Code);
        Assert.Empty(displays.ModeWrites);
        Assert.Null(recovery.Latest);
        Assert.Equal(AuditOutcome.Error, h.Audit.Final(r.RequestId).Outcome);
    }

    [Fact]
    public async Task A_restore_still_runs_when_the_display_became_primary_but_not_over_a_newer_mode()
    {
        var (h, displays, recovery, caller) = Setup();
        await Set(h, caller, FakeDisplays.Mode720p60);
        displays.OnPrepare = _ =>
        {
            displays.Replace("display-a", d => d with { IsPrimary = false });
            displays.Replace(Secondary, d => d with { IsPrimary = true });
        };

        var restored = await RestoreLocally(h);
        // Applied, and the primary moving to it is reported as a change of the layout (partial), not hidden.
        Assert.Equal(ToolStatus.Partial, restored.Status);
        Assert.Equal(FakeDisplays.Mode1080p60, displays.CurrentMode(Secondary));
        displays.OnPrepare = null;

        Assert.True((await Set(h, caller, FakeDisplays.Mode720p60, "display-a")).IsSuccess);
        var record = recovery.Latest!;
        displays.OnPrepare = id => displays.Modes[id] = displays.Modes[id] with { Current = FakeDisplays.Mode1080p144 };
        var r = await RestoreLocally(h);
        Assert.Equal(ErrorCodes.TargetChanged, r.Error?.Code);
        Assert.Equal(FakeDisplays.Mode1080p144, displays.CurrentMode("display-a"));
        Assert.Same(record, recovery.Latest);
    }

    [Fact]
    public async Task A_source_reassigned_before_the_read_back_is_unknown_and_keeps_the_record_unchanged()
    {
        var (h, displays, recovery, caller) = Setup();
        displays.OnModeApply = id => displays.ReassignedSources.Add(id);

        var r = await Set(h, caller, FakeDisplays.Mode720p60);

        Assert.Equal(ToolStatus.Unknown, r.Status);
        Assert.Equal(ErrorCodes.TargetChanged, r.Error?.Code);
        Assert.NotEqual(Verification.Verified, r.Verification);
        Assert.Equal(new DisplayModeRecord(Secondary, "DELL P2314H", FakeDisplays.Mode1080p60, FakeDisplays.Mode720p60), recovery.Latest);
    }

    [Fact]
    public async Task A_restore_never_reads_another_monitor_through_a_reassigned_source()
    {
        var (h, displays, recovery, caller) = Setup();
        await Set(h, caller, FakeDisplays.Mode720p60);
        displays.ReassignedSources.Add(Secondary);

        var r = await RestoreLocally(h);

        Assert.Equal(ErrorCodes.TargetChanged, r.Error?.Code);
        Assert.Single(displays.ModeWrites);
        Assert.NotNull(recovery.Latest);
    }
}
