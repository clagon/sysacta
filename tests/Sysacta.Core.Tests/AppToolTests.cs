using System.Text.Json;
using Sysacta.Core.Auditing;
using Sysacta.Core.Security;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Apps;
using Sysacta.Tests;
using static Sysacta.Core.Tests.AppTestKit;

namespace Sysacta.Core.Tests;

/// <summary>Phase 2A app/process tools through the executor, with a fake OS (nothing real is launched or terminated).</summary>
public class AppToolTests
{
    // ---- contract ---------------------------------------------------------------------------------------------

    [Fact]
    public void Descriptors_follow_the_spec_defaults()
    {
        var s = Create();
        var expected = new Dictionary<string, (DefaultPolicy, bool)>
        {
            ["app_list_available"] = (DefaultPolicy.On, true),
            ["app_list_running"] = (DefaultPolicy.On, true),
            ["app_launch"] = (DefaultPolicy.On, false),
            ["app_close"] = (DefaultPolicy.Off, false),
            ["process_list_running"] = (DefaultPolicy.On, true),
            ["process_terminate"] = (DefaultPolicy.Off, false),
        };
        foreach (var (name, (policy, readOnly)) in expected)
        {
            var d = s.H.Executor.Registry.Find(name)!.Descriptor;
            Assert.Equal((policy, readOnly, ToolCategories.Apps, false, null), (d.DefaultPolicy, d.ReadOnly, d.Category, d.LocalOnly, d.Transition));
        }

        Assert.Equal(["pid"], s.H.Executor.Registry.Find("process_terminate")!.Descriptor.AuditedArguments);
        Assert.Equal(["limit"], s.H.Executor.Registry.Find("process_list_running")!.Descriptor.AuditedArguments);
        Assert.Empty(s.H.Executor.Registry.Find("app_launch")!.Descriptor.AuditedArguments);
    }

    [Fact]
    public void Schemas_are_strict_and_expose_no_path_argument_or_command()
    {
        var s = Create();
        foreach (var name in All)
        {
            var tool = s.H.Executor.Registry.Find(name)!;
            var schema = tool.InputSchema;
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
            var text = schema.GetRawText();
            foreach (var forbidden in new[] { "path", "args", "arguments", "command", "url", "force", "kill_tree", "exit_code", "env" })
            {
                Assert.DoesNotContain($"\"{forbidden}\"", text);
            }

            Assert.Equal(JsonValueKind.Object, tool.OutputSchema.ValueKind);
        }

        var launch = s.H.Executor.Registry.Find("app_launch")!.InputSchema;
        Assert.Equal(AppRules.AppIdPattern, launch.GetProperty("properties").GetProperty("app_id").GetProperty("pattern").GetString());
        var terminate = s.H.Executor.Registry.Find("process_terminate")!.InputSchema;
        Assert.Equal(["pid", "start_time"], terminate.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).Order());
    }

    [Theory]
    [InlineData("app_launch", "{}")]
    [InlineData("app_launch", "{\"app_id\":null}")]
    [InlineData("app_launch", "{\"app_id\":\"notepad\"}")]
    [InlineData("app_launch", "{\"app_id\":\"C:\\\\Windows\\\\notepad.exe\"}")]
    [InlineData("app_launch", "{\"app_id\":\"app_0123456789abcdef0123456789ABCDEF\"}")]
    [InlineData("app_launch", "{\"app_id\":\"app_0123456789abcdef0123456789abcdef\",\"args\":[\"x\"]}")]
    [InlineData("app_launch", "{\"app_id\":\"app_0123456789abcdef0123456789abcdef\",\"path\":\"C:\\\\x.exe\"}")]
    [InlineData("app_close", "{\"app_id\":\"all\"}")]
    [InlineData("app_close", "{\"app_id\":\"app_0123456789abcdef0123456789abcdef\",\"force\":true}")]
    [InlineData("process_terminate", "{\"pid\":10}")]
    [InlineData("process_terminate", "{\"pid\":0,\"start_time\":\"2026-11-28T10:25:01.1234567Z\"}")]
    [InlineData("process_terminate", "{\"pid\":\"10\",\"start_time\":\"2026-11-28T10:25:01.1234567Z\"}")]
    [InlineData("process_terminate", "{\"pid\":10,\"start_time\":\"2026-11-28T10:25:01Z\"}")]
    [InlineData("process_terminate", "{\"pid\":10,\"start_time\":\"2026-11-28T10:25:01.1234567+09:00\"}")]
    [InlineData("process_terminate", "{\"pid\":10,\"start_time\":\"2026-11-28T10:25:01.1234567Z\",\"kill_tree\":true}")]
    [InlineData("process_terminate", "{\"pid\":10,\"start_time\":\"2026-11-28T10:25:01.1234567Z\",\"exit_code\":0}")]
    [InlineData("process_list_running", "{\"limit\":0}")]
    [InlineData("process_list_running", "{\"limit\":257}")]
    [InlineData("process_list_running", "{\"filter\":\"\"}")]
    [InlineData("process_list_running", "{\"filter\":\"   \"}")]
    [InlineData("process_list_running", "{\"filter\":\"a\\u0000b\"}")]
    [InlineData("process_list_running", "{\"filter\":null}")]
    [InlineData("app_list_available", "{\"x\":1}")]
    public async Task Invalid_or_extra_arguments_are_refused_before_any_os_access(string tool, string json)
    {
        var (s, _) = WithApp();
        var r = await s.Raw(tool, json);
        AssertError(r, ErrorCodes.InvalidArgument);
        Assert.Equal((0, 0, 0), (s.Os.Scans, s.Os.Prepared, s.Os.Opened));
    }

    // ---- app_list_available -----------------------------------------------------------------------------------

    [Fact]
    public async Task List_available_returns_only_allowed_apps_with_their_file_status()
    {
        var s = Create();
        var ready = s.Registry.Add(1);
        s.Os.AddFile(ready);
        var changed = s.Registry.Add(2);
        s.Os.Files[changed.Path] = new ExecutableCheck(ExecutableRefusal.None, changed.Path, FakeAppRegistry.Identity(9));
        var missing = s.Registry.Add(3);
        var unsupported = s.Registry.Add(4, name: "Bad");
        s.Os.Files[unsupported.Path] = ExecutableCheck.Refused(ExecutableRefusal.DeniedProgram);
        var notAllowed = s.Registry.Add(5, allowed: false);
        s.Os.AddFile(notAllowed);

        var r = await s.Call("app_list_available");
        Assert.True(r.IsSuccess);
        var apps = r.Data!["apps"]!.AsArray().ToDictionary(a => (string)a!["app_id"]!, a => ((string)a!["status"]!, (bool)a["launchable"]!));
        Assert.Equal(4, apps.Count);
        Assert.Equal(("ready", true), apps[ready.Id]);
        Assert.Equal(("target_changed", false), apps[changed.Id]);
        Assert.Equal(("target_missing", false), apps[missing.Id]);
        Assert.Equal(("not_supported", false), apps[unsupported.Id]);
        Assert.False(apps.ContainsKey(notAllowed.Id));
        Assert.True((bool)r.Data["launch_available"]!);
        var text = r.Data.ToJsonString();
        Assert.DoesNotContain(@"C:\\Apps", text);
        Assert.DoesNotContain(ready.Identity.Sha256, text);
    }

    [Fact]
    public async Task List_available_is_empty_without_allowed_apps_and_hides_unsafe_names()
    {
        var s = Create();
        var r = await s.Call("app_list_available");
        Assert.Equal(0, (int)r.Data!["count"]!);
        Assert.False((bool)r.Data["truncated"]!);
        var app = s.Registry.Add(1, name: "evil http://x");
        s.Os.AddFile(app);
        r = await s.Call("app_list_available");
        Assert.Null(r.Data!["apps"]![0]!["display_name"]);
    }

    [Fact]
    public async Task Elevated_sysacta_hides_launch_and_reports_why()
    {
        var (s, app) = WithApp();
        s.Os.IsElevated = true;
        var r = await s.Call("app_list_available");
        Assert.False((bool)r.Data!["launch_available"]!);
        Assert.Equal("sysacta_elevated", (string)r.Data["launch_blocked_reason"]!);
        Assert.False((bool)r.Data["apps"]![0]!["launchable"]!);
        Assert.DoesNotContain(s.H.Executor.ListTools(s.Caller), t => t.Name == "app_launch");
        var launch = await s.Launch(app);
        AssertError(launch, ErrorCodes.NotSupported);
        Assert.Equal((0, 0), (s.Os.Prepared, s.Os.Launches));
    }

    // ---- app_launch -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Launch_starts_the_approved_target_once_and_reports_accepted()
    {
        var (s, app) = WithApp("--profile", "secret-arg-XYZ");
        var r = await s.Launch(app);
        Assert.True(r.IsSuccess);
        Assert.Equal(Verification.Accepted, r.Verification);
        Assert.Equal((true, false, 4242), ((bool)r.Data!["launched"]!, (bool)r.Data["already_running"]!, (int)r.Data["pid"]!));
        Assert.Equal(ProcessStartTime.ToText(s.Os.NextLaunch.CreationTime!.Value), (string)r.Data["start_time"]!);
        Assert.Equal((1, 1, 1), (s.Os.Prepared, s.Os.Launches, s.Os.Disposed));
        var final = s.H.Audit.Final(r.RequestId);
        Assert.Equal(AuditOutcome.Success, final.Outcome);
        Assert.Equal($"{{\"target\":\"{app.Id}\"}}", final.Arguments);
        var everything = JsonSerializer.Serialize(s.H.Audit.Events) + r.Data.ToJsonString();
        Assert.DoesNotContain("secret-arg-XYZ", everything);
        Assert.DoesNotContain("Apps", everything);
        Assert.DoesNotContain(app.Identity.Sha256, everything);
    }

    [Fact]
    public async Task Launch_is_a_verified_no_op_when_an_instance_already_runs()
    {
        var (s, app) = WithApp();
        s.RunManaged(app, 100);
        var r = await s.Launch(app);
        Assert.True(r.IsSuccess);
        Assert.Equal(Verification.Verified, r.Verification);
        Assert.True((bool)r.Data!["already_running"]!);
        Assert.Equal((0, 0), (s.Os.Prepared, s.Os.Launches));
    }

    [Fact]
    public async Task An_instance_sysacta_did_not_launch_is_neither_verified_nor_launched_again()
    {
        var (s, app) = WithApp();
        s.Os.Run(app, 100); // started by the user: only estimated
        var r = await s.Launch(app);
        AssertError(r, ErrorCodes.AmbiguousTarget);
        Assert.Equal((0, 0), (s.Os.Prepared, s.Os.Launches));

        // Launched by Sysacta under a previous approval: no longer managed after a re-approval.
        s.Os.Processes.Clear();
        s.RunManaged(app, 101);
        s.Registry.Reapprove(app.Id);
        AssertError(await s.Launch(app), ErrorCodes.AmbiguousTarget);
        Assert.Equal(0, s.Os.Launches);
    }

    [Fact]
    public async Task A_launched_process_becomes_managed_and_can_then_be_terminated()
    {
        var (s, app) = WithApp();
        var r = await s.Launch(app);
        Assert.True((bool)r.Data!["launched"]!);
        var p = s.Os.Run(app, 4242, creation: s.Os.NextLaunch.CreationTime!.Value);
        var listed = (await s.Call("app_list_running")).Data!["instances"]![0]!;
        Assert.Equal(("managed", true), ((string)listed["app_match"]!, (bool)listed["can_terminate"]!));
        Assert.True((await s.Launch(app)).IsSuccess); // already running (managed): no-op
        Assert.Equal(1, s.Os.Launches);
        var t = await s.Terminate(p);
        Assert.Equal(Verification.Verified, t.Verification);
        Assert.Equal(0, s.Managed.Count);
    }

    [Fact]
    public async Task A_same_named_process_of_another_executable_is_not_the_app()
    {
        var (s, app) = WithApp();
        s.Os.Processes[50] = new FakeProcess(50, 1, app.FileName, FakeAppRegistry.Identity(7));
        var r = await s.Launch(app);
        Assert.True((bool)r.Data!["launched"]!);
    }

    [Fact]
    public async Task Launch_refuses_when_running_state_is_unknown()
    {
        var (s, app) = WithApp();
        s.Os.Uncertain.Add(app.Id);
        var r = await s.Launch(app);
        AssertError(r, ErrorCodes.OsApiError);
        Assert.True(r.Error!.Retryable);
        Assert.Equal(0, s.Os.Launches);
    }

    [Fact]
    public async Task Launch_refuses_unknown_disabled_and_removed_apps()
    {
        var (s, app) = WithApp();
        AssertError(await s.Call("app_launch", new { app_id = AppRules.NewAppId() }), ErrorCodes.TargetNotFound);
        s.Registry.Disable(app.Id);
        AssertError(await s.Launch(app), ErrorCodes.TargetNotFound);
        s.Registry.Remove(app.Id);
        AssertError(await s.Launch(app), ErrorCodes.TargetNotFound);
        Assert.Equal(0, s.Os.Prepared);
    }

    [Fact]
    public async Task Changed_executable_is_refused_and_never_launched()
    {
        var (s, app) = WithApp();
        s.Os.Files[app.Path] = new ExecutableCheck(ExecutableRefusal.None, app.Path, FakeAppRegistry.Identity(8));
        var r = await s.Launch(app);
        AssertError(r, ErrorCodes.TargetChanged);
        Assert.Equal(0, s.Os.Launches);
    }

    [Theory]
    [MemberData(nameof(AppTestKit.RaceKinds), MemberType = typeof(AppTestKit))]
    public async Task A_change_completed_during_preparation_refuses_the_launch(string kind)
    {
        var (s, app) = WithApp();
        s.Os.OnPrepare = () => s.H.Update(() => Apply(s, app, kind));
        var r = await s.Launch(app);
        Assert.Equal(ToolStatus.Error, r.Status);
        Assert.Contains(r.Error!.Code, new[] { ErrorCodes.PermissionDenied, ErrorCodes.ToolDisabled });
        Assert.Equal(0, s.Os.Launches);
        Assert.Equal(1, s.Os.Disposed);
        Assert.Equal(AuditOutcome.Denied, s.H.Audit.Final(r.RequestId).Outcome);
    }

    [Fact]
    public async Task Re_approval_while_waiting_never_switches_to_the_new_approval()
    {
        var (s, app) = WithApp();
        // Runs while the pending audit record is written: after resolution and locking, before execution.
        s.H.Audit.OnWrite = e => { if (e.Outcome == AuditOutcome.Pending) s.Registry.Reapprove(app.Id); };
        var r = await s.Launch(app);
        AssertError(r, ErrorCodes.TargetChanged);
        Assert.Equal((0, 0), (s.Os.Prepared, s.Os.Launches));
    }

    [Fact]
    public async Task The_final_check_reads_the_registry_inside_the_start_point()
    {
        var (s, app) = WithApp();
        await s.Launch(app);
        // At the start point and again right before the created process is resumed.
        Assert.Equal(2, s.Registry.CurrentChecks);
    }

    [Fact]
    public async Task Cancellation_during_preparation_never_launches_later()
    {
        var (s, app) = WithApp();
        using var cts = new CancellationTokenSource();
        var released = new ManualResetEventSlim();
        s.Os.OnPrepare = () => { cts.Cancel(); released.Wait(Ct); };
        var call = s.Call("app_launch", new { app_id = app.Id }, cts.Token);
        var r = await call;
        AssertError(r, ErrorCodes.Cancelled);
        Assert.True(r.Error!.Retryable);
        released.Set();
        await WaitUntil(() => s.Os.Disposed == 1);
        Assert.Equal(0, s.Os.Launches);
    }

    [Fact]
    public async Task Failed_audit_or_power_transition_refuses_before_any_os_change()
    {
        var (s, app) = WithApp();
        s.H.Audit.Fail = true;
        AssertError(await s.Launch(app), ErrorCodes.AuditUnavailable);
        s.H.Audit.Fail = false;
        Assert.True(s.H.Power.TryEnterTransition(PowerTransition.Shutdown));
        AssertError(await s.Launch(app), ErrorCodes.Busy);
        Assert.Equal((0, 0), (s.Os.Prepared, s.Os.Launches));
    }

    [Theory]
    [InlineData(LaunchStatus.Failed, "OS_API_ERROR")]
    [InlineData(LaunchStatus.ElevationRequired, "NOT_SUPPORTED")]
    [InlineData(LaunchStatus.ImageMismatch, "TARGET_CHANGED")]
    public async Task Launch_failures_report_that_nothing_was_started(LaunchStatus status, string code)
    {
        var (s, app) = WithApp();
        s.Os.NextLaunch = new LaunchResult(status, Reclaimed: status == LaunchStatus.ImageMismatch ? true : null);
        var r = await s.Launch(app);
        AssertError(r, code);
        Assert.False((bool)r.Data!["launched"]!);
        Assert.Equal(1, s.Os.Launches);
    }

    [Theory]
    [MemberData(nameof(AppTestKit.RaceKinds), MemberType = typeof(AppTestKit))]
    public async Task A_change_after_creation_but_before_resume_removes_the_suspended_process(string kind)
    {
        var (s, app) = WithApp();
        s.Os.OnWrite = () => s.H.Update(() => Apply(s, app, kind));
        var r = await s.Launch(app);
        Assert.Equal(ToolStatus.Error, r.Status);
        Assert.Contains(r.Error!.Code, new[] { ErrorCodes.PermissionDenied, ErrorCodes.ToolDisabled });
        Assert.False((bool)r.Data!["launched"]!);
        Assert.Equal((1, 0, 1), (s.Os.Launches, s.Os.Resumes, s.Os.Reclaims));
    }

    [Fact]
    public async Task Cancellation_after_creation_never_resumes_the_process()
    {
        var (s, app) = WithApp();
        using var cts = new CancellationTokenSource();
        s.Os.OnWrite = cts.Cancel;
        var r = await s.Call("app_launch", new { app_id = app.Id }, cts.Token);
        Assert.Equal(ToolStatus.Unknown, r.Status);
        await WaitUntil(() => s.Os.Reclaims == 1);
        Assert.Equal(0, s.Os.Resumes);
    }

    [Theory]
    [InlineData(LaunchStatus.ImageMismatch)]
    [InlineData(LaunchStatus.ResumeFailed)]
    [InlineData(LaunchStatus.Started)]
    public async Task An_unconfirmed_removal_of_a_created_process_is_unknown(LaunchStatus status)
    {
        var (s, app) = WithApp();
        s.Os.ReclaimSucceeds = false;
        if (status == LaunchStatus.Started)
        {
            s.Os.OnWrite = () => s.H.Update(() => s.Registry.Disable(app.Id)); // withdrawn, removal not confirmed
        }
        else
        {
            s.Os.NextLaunch = new LaunchResult(status, 4242, 1, Reclaimed: false);
        }

        var r = await s.Launch(app);
        Assert.Equal(ToolStatus.Unknown, r.Status);
        Assert.False(r.Error!.Retryable);
        Assert.Equal(0, s.Os.Resumes);
    }

    [Fact]
    public async Task A_truncated_process_list_refuses_launch()
    {
        var (s, app) = WithApp();
        s.Os.Truncated = true;
        AssertError(await s.Launch(app), ErrorCodes.OsApiError);
        Assert.Equal(0, s.Os.Launches);
    }

    [Fact]
    public async Task An_exception_after_the_start_point_is_unknown_and_not_retryable()
    {
        var (s, app) = WithApp();
        s.Os.OnWrite = () => throw new InvalidOperationException(@"C:\secret\path");
        var r = await s.Launch(app);
        Assert.Equal(ToolStatus.Unknown, r.Status);
        Assert.False(r.Error!.Retryable);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(r));
    }

    // ---- app_list_running -------------------------------------------------------------------------------------

    [Fact]
    public async Task Running_instances_are_matched_by_the_approved_file_only()
    {
        var s = Create();
        var a = s.Registry.Add(1);
        var b = s.Registry.Add(2, allowed: false);
        s.RunManaged(a, 10);
        s.RunManaged(a, 11);
        s.Os.Run(b, 12);
        s.Os.Processes[13] = new FakeProcess(13, 5, a.FileName, FakeAppRegistry.Identity(7));   // same name, other file
        s.Os.Processes[14] = new FakeProcess(14, 5, "x.exe", a.Identity, SameUser: false);    // other user
        s.Os.Processes[15] = s.RunManaged(a, 15) with { Rid = 0x3000 };
        s.Os.Run(a, 16); // started by the user
        s.Os.Uncertain.Add(a.Id);
        var r = await s.Call("app_list_running");
        var instances = r.Data!["instances"]!.AsArray();
        Assert.Equal([10, 11, 15, 16], instances.Select(i => (int)i!["pid"]!));
        Assert.All(instances, i => Assert.Equal(a.Id, (string)i!["app_id"]!));
        Assert.True((bool)instances[0]!["can_terminate"]!);
        Assert.Equal("elevated", (string)instances[2]!["terminate_blocked_reason"]!);
        Assert.Equal(("estimated", false, "not_managed"), ((string)instances[3]!["app_match"]!, (bool)instances[3]!["can_terminate"]!, (string)instances[3]!["terminate_blocked_reason"]!));
        Assert.Equal([a.Id], r.Data["uncertain_app_ids"]!.AsArray().Select(x => (string)x!));
    }
}
