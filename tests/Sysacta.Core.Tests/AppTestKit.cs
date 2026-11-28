using Sysacta.Core.Security;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Apps;
using Sysacta.Tests;

namespace Sysacta.Core.Tests;

/// <summary>Shared setup of the Phase 2A tool tests: every app/process tool over a fake registry and a fake OS.</summary>
internal static class AppTestKit
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static readonly string[] All = ["app_list_available", "app_list_running", "app_launch", "app_close", "process_list_running", "process_terminate"];

    public sealed record Setup(Harness H, FakeAppRegistry Registry, FakeAppPlatform Os, ManagedProcesses Managed, Caller Caller)
    {
        public Task<ToolResult> Call(string tool, object? args = null, CancellationToken? ct = null) =>
            H.Executor.ExecuteAsync(Caller, tool, args is null ? null : TestJson.Args(args), ct ?? Ct);

        public Task<ToolResult> Raw(string tool, string json) => H.Executor.ExecuteAsync(Caller, tool, TestJson.Raw(json), Ct);

        public Task<ToolResult> Launch(RegisteredApp app) => Call("app_launch", new { app_id = app.Id });

        public Task<ToolResult> Close(RegisteredApp app) => Call("app_close", new { app_id = app.Id });

        public Task<ToolResult> Terminate(FakeProcess p) => Call("process_terminate", new { pid = p.Pid, start_time = ProcessStartTime.ToText(p.Creation) });

        public int OsWrites => Os.Launches + Os.CloseRequests + Os.Terminations;

        /// <summary>A process of the app as if Sysacta had launched it under the current approval (managed).</summary>
        public FakeProcess RunManaged(RegisteredApp app, int pid)
        {
            var p = Os.Run(app, pid);
            Managed.Add(app.Id, Registry.Apps[app.Id].Generation, p.Pid, p.Creation);
            return p;
        }
    }

    public static Setup Create()
    {
        var registry = new FakeAppRegistry();
        var os = new FakeAppPlatform();
        var managed = new ManagedProcesses();
        var h = new Harness(_ =>
        [
            new AppListAvailableTool(registry, os), new AppListRunningTool(registry, os, managed), new AppLaunchTool(registry, os, managed),
            new AppCloseTool(registry, os, managed), new ProcessListRunningTool(registry, os, managed), new ProcessTerminateTool(registry, os, managed),
        ]);
        var (caller, _) = h.Store.AddClient("c1", All);
        return new(h, registry, os, managed, caller);
    }

    public static (Setup S, RegisteredApp App) WithApp(params string[] args)
    {
        var s = Create();
        var app = s.Registry.Add(1, args: args);
        s.Os.AddFile(app);
        return (s, app);
    }

    public static void AssertError(ToolResult r, string code)
    {
        Assert.Equal(ToolStatus.Error, r.Status);
        Assert.Equal(code, r.Error!.Code);
    }


    public static TheoryData<string> RaceKinds => ["disable", "remove", "reapprove", "revoke", "reissue", "tool_disabled", "grant_removed"];

    public static void Apply(Setup s, RegisteredApp app, string kind)
    {
        switch (kind)
        {
            case "disable": s.Registry.Disable(app.Id); break;
            case "remove": s.Registry.Remove(app.Id); break;
            case "reapprove": s.Registry.Reapprove(app.Id); break;
            case "revoke": s.H.Store.Revoke("c1"); break;
            case "reissue": s.H.Store.Reissue("c1"); break;
            case "tool_disabled": s.H.Store.EnabledTools.Clear(); break;
            case "grant_removed": s.H.Store.Grants.Clear(); break;
        }
    }


    public static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, Ct);
        }

        Assert.True(condition());
    }
}
