using System.Text.Json;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Apps;
using Sysacta.Tests;
using static Sysacta.Core.Tests.AppTestKit;

namespace Sysacta.Core.Tests;

/// <summary>Phase 2A (B): normal close, process list and forced termination through the executor, with a fake OS.</summary>
public class ProcessToolTests
{
    // ---- app_close --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Close_posts_one_request_and_verifies_exit_through_the_handle()
    {
        var (s, app) = WithApp();
        s.RunManaged(app, 10);
        var r = await s.Close(app);
        Assert.True(r.IsSuccess);
        Assert.Equal(Verification.Verified, r.Verification);
        Assert.Equal((true, true, true), ((bool)r.Data!["was_running"]!, (bool)r.Data["close_requested"]!, (bool)r.Data["exited"]!));
        Assert.Equal((1, 0), (s.Os.CloseRequests, s.Os.Terminations));
        Assert.Contains("|10|", s.H.Audit.Final(r.RequestId).Arguments);
    }

    [Fact]
    public async Task Close_reports_accepted_when_the_app_keeps_running_and_never_forces()
    {
        var (s, app) = WithApp();
        s.RunManaged(app, 10);
        s.Os.ExitsAfterRequest = false;
        var r = await s.Close(app);
        Assert.Equal(Verification.Accepted, r.Verification);
        Assert.False((bool)r.Data!["exited"]!);
        Assert.Equal((1, 0), (s.Os.CloseRequests, s.Os.Terminations));
    }

    [Fact]
    public async Task Close_of_a_stopped_app_is_a_verified_no_op()
    {
        var (s, app) = WithApp();
        var r = await s.Close(app);
        Assert.True(r.IsSuccess);
        Assert.Equal(Verification.Verified, r.Verification);
        Assert.False((bool)r.Data!["was_running"]!);
        Assert.Equal(0, s.Os.CloseRequests);
    }

    [Fact]
    public async Task Close_refuses_several_processes_windows_or_none_without_sending()
    {
        var (s, app) = WithApp();
        s.RunManaged(app, 10);
        s.RunManaged(app, 11);
        AssertError(await s.Close(app), ErrorCodes.AmbiguousTarget);
        s.Os.Processes.TryRemove(11, out _);
        s.Os.Processes[10] = s.Os.Processes[10] with { Windows = 2 };
        AssertError(await s.Close(app), ErrorCodes.NotSupported);
        s.Os.Processes[10] = s.Os.Processes[10] with { Windows = 0 };
        AssertError(await s.Close(app), ErrorCodes.NotSupported);
        s.Os.Uncertain.Add(app.Id);
        AssertError(await s.Close(app), ErrorCodes.OsApiError);
        Assert.Equal(0, s.Os.CloseRequests);
    }

    [Fact]
    public async Task Close_refuses_protected_or_elevated_instances()
    {
        var (s, app) = WithApp();
        s.Os.Processes[10] = s.RunManaged(app, 10) with { Rid = 0x3000 };
        AssertError(await s.Close(app), ErrorCodes.PermissionDenied);
        s.Os.Processes[10] = s.Os.Processes[10] with { Rid = 0x2000, Protected = null };
        AssertError(await s.Close(app), ErrorCodes.PermissionDenied);
        Assert.Equal(0, s.Os.CloseRequests);
    }

    [Fact]
    public async Task Close_never_switches_to_another_instance()
    {
        var (s, app) = WithApp();
        var first = s.RunManaged(app, 10);
        s.Os.OnOpen = () => s.Os.Processes[10] = first with { Creation = first.Creation + 1 };
        AssertError(await s.Close(app), ErrorCodes.TargetChanged);
        s.Os.OnOpen = () => s.Os.Processes.TryRemove(10, out _);
        s.RunManaged(app, 10);
        AssertError(await s.Close(app), ErrorCodes.TargetNotFound);
        Assert.Equal(0, s.Os.CloseRequests);
    }

    [Fact]
    public async Task Close_refuses_an_instance_sysacta_did_not_launch()
    {
        var (s, app) = WithApp();
        s.Os.Run(app, 10);
        AssertError(await s.Close(app), ErrorCodes.PermissionDenied);
        s.RunManaged(app, 11);
        AssertError(await s.Close(app), ErrorCodes.AmbiguousTarget); // a managed and an unmanaged instance
        Assert.Equal(0, s.Os.CloseRequests);
    }

    [Fact]
    public async Task Close_refuses_a_managed_instance_after_re_approval()
    {
        var (s, app) = WithApp();
        s.RunManaged(app, 10);
        s.Registry.Reapprove(app.Id);
        AssertError(await s.Close(app), ErrorCodes.PermissionDenied);
        Assert.Equal(0, s.Os.CloseRequests);
    }

    [Fact]
    public async Task Close_refuses_a_truncated_process_list()
    {
        var (s, app) = WithApp();
        s.RunManaged(app, 10);
        s.Os.Truncated = true;
        AssertError(await s.Close(app), ErrorCodes.OsApiError);
        Assert.Equal(0, s.Os.CloseRequests);
    }

    [Theory]
    [InlineData("second_instance", ErrorCodes.AmbiguousTarget)]
    [InlineData("replaced", ErrorCodes.TargetChanged)]
    [InlineData("exited", ErrorCodes.TargetNotFound)]
    [InlineData("uncertain", ErrorCodes.OsApiError)]
    public async Task Close_re_verifies_the_single_instance_after_waiting_for_the_locks(string change, string code)
    {
        var (s, app) = WithApp();
        s.RunManaged(app, 10);
        // Runs while the pending audit record is written: after resolution and locking, before execution.
        s.H.Audit.OnWrite = e =>
        {
            if (e.Outcome != Sysacta.Core.Auditing.AuditOutcome.Pending) return;
            switch (change)
            {
                case "second_instance": s.RunManaged(app, 11); break;
                case "replaced": s.Os.Processes.TryRemove(10, out _); s.RunManaged(app, 12); break;
                case "exited": s.Os.Processes.TryRemove(10, out _); break;
                case "uncertain": s.Os.Uncertain.Add(app.Id); break;
            }
        };
        var r = await s.Close(app);
        AssertError(r, code);
        Assert.Equal((0, 0), (s.Os.Opened, s.Os.CloseRequests));
    }

    [Fact]
    public async Task Close_reports_a_vanished_window_as_not_sent()
    {
        var (s, app) = WithApp();
        s.RunManaged(app, 10);
        s.Os.NextClose = CloseRequestStatus.WindowGone;
        var r = await s.Close(app);
        AssertError(r, ErrorCodes.TargetChanged);
        Assert.False((bool)r.Data!["close_requested"]!);
    }

    [Theory]
    [MemberData(nameof(AppTestKit.RaceKinds), MemberType = typeof(AppTestKit))]
    public async Task A_change_completed_while_opening_refuses_the_close(string kind)
    {
        var (s, app) = WithApp();
        s.RunManaged(app, 10);
        s.Os.OnOpen = () => s.H.Update(() => Apply(s, app, kind));
        var r = await s.Close(app);
        Assert.Equal(ToolStatus.Error, r.Status);
        Assert.Equal(0, s.Os.CloseRequests);
        Assert.Equal(1, s.Os.Disposed);
    }

    // ---- process_list_running ---------------------------------------------------------------------------------

    [Fact]
    public async Task Process_list_filters_sorts_limits_and_never_returns_other_users()
    {
        var s = Create();
        var app = s.Registry.Add(1, args: ["SECRETARG"]);
        s.RunManaged(app, 30);
        s.Os.Run(null, 20, "Notepad.exe");
        s.Os.Run(null, 21, "notepad.exe");
        s.Os.Run(null, 22, "zeta.exe");
        s.Os.Processes[23] = new FakeProcess(23, 1, "notepad.exe", null, SameUser: false);
        s.Os.Run(null, 24, "C:\\leak\\path.exe");
        s.Os.FailedCount = 3;

        var r = await s.Call("process_list_running", new { filter = "NOTE" });
        var names = r.Data!["processes"]!.AsArray().Select(p => ((int)p!["pid"]!, (string?)p["name"])).ToList();
        Assert.Equal([(20, "Notepad.exe"), (21, "notepad.exe")], names);
        Assert.Equal(3, (int)r.Data["failed_count"]!);

        r = await s.Call("process_list_running", new { limit = 2 });
        Assert.Equal(2, r.Data!["processes"]!.AsArray().Count);
        Assert.Equal(5, (int)r.Data["total"]!);
        Assert.True((bool)r.Data["truncated"]!);

        r = await s.Call("process_list_running");
        var all = r.Data!["processes"]!.AsArray();
        Assert.DoesNotContain(all, p => (int)p!["pid"]! == 23);
        var mine = all.Single(p => (int)p!["pid"]! == 30)!;
        Assert.Equal((app.Id, true), ((string)mine["app_id"]!, (bool)mine["can_terminate"]!));
        var other = all.Single(p => (int)p!["pid"]! == 20)!;
        Assert.Equal("not_registered", (string)other["terminate_blocked_reason"]!);
        Assert.Null(all.Single(p => (int)p!["pid"]! == 24)!["name"]);
        Assert.DoesNotContain("leak", r.Data.ToJsonString());
        Assert.DoesNotContain("SECRETARG", r.Data.ToJsonString());
        Assert.Equal(["{\"limit\":null}", "{\"limit\":2}", "{\"limit\":null}"], s.H.Audit.Events.Select(e => e.Arguments));
    }

    [Fact]
    public async Task Process_filter_text_is_never_audited()
    {
        var s = Create();
        await s.Call("process_list_running", new { filter = "SECRET-FILTER" });
        Assert.DoesNotContain("SECRET-FILTER", JsonSerializer.Serialize(s.H.Audit.Events));
    }

    // ---- process_terminate ------------------------------------------------------------------------------------

    [Fact]
    public async Task Terminate_acts_once_on_the_verified_handle_and_verifies_exit()
    {
        var (s, app) = WithApp();
        var p = s.RunManaged(app, 10);
        var r = await s.Terminate(p);
        Assert.True(r.IsSuccess);
        Assert.Equal(Verification.Verified, r.Verification);
        Assert.Equal((true, true), ((bool)r.Data!["terminated"]!, (bool)r.Data["exited"]!));
        Assert.Equal((1, 1, 0), (s.Os.Terminations, s.Os.Disposed, s.Os.CloseRequests));
        var audit = s.H.Audit.Final(r.RequestId).Arguments!;
        Assert.Contains("\"pid\":10", audit);
        Assert.Contains($"{app.Id}|10|{ProcessStartTime.ToText(p.Creation)}", audit);
    }

    [Fact]
    public async Task Terminate_reports_accepted_without_an_exit_signal()
    {
        var (s, app) = WithApp();
        s.Os.ExitsAfterRequest = false;
        var r = await s.Terminate(s.RunManaged(app, 10));
        Assert.Equal(Verification.Accepted, r.Verification);
    }

    [Fact]
    public async Task Pid_reuse_and_exit_are_detected_by_the_exact_start_time()
    {
        var (s, app) = WithApp();
        var p = s.RunManaged(app, 10);
        AssertError(await s.Terminate(p with { Creation = p.Creation + 1 }), ErrorCodes.TargetChanged);
        AssertError(await s.Terminate(p with { Pid = 99 }), ErrorCodes.TargetNotFound);
        // Reused after resolution, before the handle was opened.
        s.Os.OnOpen = () => s.Os.Processes[10] = p with { Creation = p.Creation + 10 };
        AssertError(await s.Terminate(p), ErrorCodes.TargetChanged);
        Assert.Equal(0, s.Os.Terminations);
    }

    public static TheoryData<string> Blocks => ["self", "parent", "protected", "critical", "elevated", "low", "unverified", "unregistered", "other_user", "not_allowed", "not_managed", "reapproved"];

    [Theory]
    [MemberData(nameof(Blocks))]
    public async Task Protected_and_unregistered_processes_are_never_terminated(string block)
    {
        var (s, app) = WithApp();
        var p = s.RunManaged(app, 10);
        s.Os.Processes[10] = block switch
        {
            "self" => p with { IsSelf = true },
            "parent" => p with { IsParent = true },
            "protected" => p with { Protected = true },
            "critical" => p with { Critical = true },
            "elevated" => p with { Rid = 0x3000 },
            "low" => p with { Rid = 0x1000 },
            "unverified" => p with { Critical = null },
            "unregistered" => p with { Image = FakeAppRegistry.Identity(5) },
            "not_managed" => p,
            "other_user" => p with { SameUser = false },
            _ => p,
        };
        if (block == "reapproved")
        {
            s.Registry.Reapprove(app.Id); // launched under the previous approval
        }

        if (block == "not_allowed")
        {
            s.Registry.Disable(app.Id);
        }

        if (block is "not_managed" or "unregistered")
        {
            s.Managed.Remove(p.Pid, p.Creation); // only observed, never launched by Sysacta
        }

        var r = await s.Terminate(s.Os.Processes[10]);
        Assert.Equal(ToolStatus.Error, r.Status);
        Assert.Contains(r.Error!.Code, new[] { ErrorCodes.PermissionDenied, ErrorCodes.TargetNotFound });
        Assert.Equal(0, s.Os.Terminations);
    }

    [Fact]
    public async Task Protection_detected_only_at_open_still_refuses()
    {
        var (s, app) = WithApp();
        var p = s.RunManaged(app, 10);
        s.Os.OnOpen = () => s.Os.Processes[10] = p with { Protected = true };
        AssertError(await s.Terminate(p), ErrorCodes.PermissionDenied);
        Assert.Equal((0, 1), (s.Os.Terminations, s.Os.Disposed));
    }

    [Theory]
    [MemberData(nameof(AppTestKit.RaceKinds), MemberType = typeof(AppTestKit))]
    public async Task A_change_completed_while_opening_refuses_the_termination(string kind)
    {
        var (s, app) = WithApp();
        var p = s.RunManaged(app, 10);
        s.Os.OnOpen = () => s.H.Update(() => Apply(s, app, kind));
        var r = await s.Terminate(p);
        Assert.Equal(ToolStatus.Error, r.Status);
        Assert.Equal(0, s.Os.Terminations);
    }

    [Fact]
    public async Task A_refused_termination_and_an_exception_are_reported_honestly()
    {
        var (s, app) = WithApp();
        var p = s.RunManaged(app, 10);
        s.Os.TerminateAccepted = false;
        var r = await s.Terminate(p);
        AssertError(r, ErrorCodes.OsApiError);
        Assert.False((bool)r.Data!["terminated"]!);
        s.Os.TerminateAccepted = true;
        s.Os.OnWrite = () => throw new IOException("boom");
        r = await s.Terminate(p);
        Assert.Equal(ToolStatus.Unknown, r.Status);
        Assert.False(r.Error!.Retryable);
        Assert.Equal(2, s.Os.Terminations);
    }

    // ---- shared locks -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Launch_close_and_terminate_of_one_app_are_serialized_and_other_apps_run_in_parallel()
    {
        var s = Create();
        var a = s.Registry.Add(1);
        var b = s.Registry.Add(2);
        s.Os.AddFile(a);
        s.Os.AddFile(b);
        var p = s.RunManaged(a, 10);
        using var release = new SemaphoreSlim(0);
        var entered = new TaskCompletionSource();
        s.Os.OnWrite = () =>
        {
            if (s.Os.Terminations == 1 && s.Os.Launches == 0)
            {
                entered.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(30), Ct)); // bounded: a broken test fails instead of hanging
            }
        };

        var terminate = s.Terminate(p);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var scans = s.Os.Scans;
        var closeA = s.Close(a);            // same app: waits for the app lock
        var launchB = await s.Launch(b);    // other app: independent
        Assert.True(launchB.IsSuccess);
        await Task.Delay(100, Ct);
        Assert.False(closeA.IsCompleted);
        Assert.Equal(scans + 2, s.Os.Scans); // close resolution + launch B; close execution has not started
        release.Release();
        Assert.True((await terminate).IsSuccess);
        await closeA;
        Assert.Equal(0, s.H.Executor.TrackedLockCount);
    }
}
