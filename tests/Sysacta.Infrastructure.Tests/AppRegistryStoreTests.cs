using Sysacta.Core.Security;
using Sysacta.Core.Tools.Apps;

namespace Sysacta.Infrastructure.Tests;

/// <summary>Phase 2A allow-list storage: migration of old rows, registration, approval, generations and gate ordering.</summary>
public class AppRegistryStoreTests
{
    private const string Path1 = @"C:\Apps\one\one.exe";
    private const string Path2 = @"C:\Apps\two\two.exe";

    private static ExecutableIdentity Id(int n) => new($"{n:x16}", $"{n:x32}", 1000 + n, new string((char)('a' + n % 6), 64));

    private static (TempDatabase T, AppRegistryStore Store, AuthorizationGate Gate) Create()
    {
        var t = new TempDatabase();
        t.Database.Migrate();
        var gate = new AuthorizationGate();
        return (t, new AppRegistryStore(t.Database, gate), gate);
    }

    [Fact]
    public void Rows_from_before_the_upgrade_are_disabled_and_never_resolvable()
    {
        using var t = new TempDatabase();
        t.Database.Migrate([.. SysactaDatabase.Migrations.Take(2)]);
        using (var c = t.Database.Open())
        {
            SysactaDatabase.Execute(c, "INSERT INTO registered_apps (id, display_name, launch_target, fixed_args, allowed) VALUES ('legacy', 'Old', 'cmd.exe /c calc', '/x', 1)");
        }

        t.Database.Migrate();
        var store = new AppRegistryStore(t.Database, new AuthorizationGate());
        var row = Assert.Single(store.ListAll());
        Assert.Equal(("legacy", false, null), (row.Id, row.Allowed, row.App));
        Assert.Empty(store.ListAllowed());
        Assert.False(store.IsCurrent("legacy", 0));
        Assert.Throws<InvalidOperationException>(() => store.SetAllowed("legacy", 0, true));
        Assert.Throws<InvalidOperationException>(() => store.Reapprove("legacy", 0, Path1, [], Id(1)));
        store.Delete("legacy");
        Assert.Empty(store.ListAll());
    }

    [Fact]
    public void Registration_is_not_allowed_until_explicitly_allowed()
    {
        var (t, store, _) = Create();
        using var _t = t;
        var id = store.Register(Path1, "  One  ", ["--a", ""], Id(1));
        Assert.True(AppRules.IsAppId(id));
        var row = Assert.Single(store.ListAll());
        Assert.Equal(("One", false, 1L), (row.DisplayName, row.Allowed, row.App!.Generation));
        Assert.Equal(["--a", ""], row.App.FixedArgs);
        Assert.Empty(store.ListAllowed());
        Assert.False(store.IsCurrent(id, 1));

        store.SetAllowed(id, 1, true);
        var app = Assert.Single(store.ListAllowed());
        Assert.Equal((id, Path1, Id(1)), (app.Id, app.Path, app.Identity));
        Assert.True(store.IsCurrent(id, 1));
        Assert.False(store.IsCurrent(id, 2));

        Assert.Equal(2, store.SetAllowed(id, 1, false));
        Assert.Empty(store.ListAllowed());
        Assert.False(store.IsCurrent(id, 1));

        // Allowed again: a call that resolved generation 1 stays refused.
        Assert.Equal(2, store.SetAllowed(id, 2, true));
        Assert.False(store.IsCurrent(id, 1));
        Assert.True(store.IsCurrent(id, 2));
        Assert.Throws<InvalidOperationException>(() => store.SetAllowed(id, 1, true));
    }

    [Fact]
    public void One_executable_file_has_one_registration()
    {
        var (t, store, _) = Create();
        using var _t = t;
        var first = store.Register(Path1, "One", [], Id(1));
        // Same volume + file id under another path (hard link, other spelling) or the same path.
        Assert.Throws<ArgumentException>(() => store.Register(Path2, "Two", [], Id(1) with { Sha256 = new string('f', 64) }));
        Assert.Throws<ArgumentException>(() => store.Register(Path1, "One again", ["x"], Id(1)));
        var second = store.Register(Path2, "Two", [], Id(2));
        Assert.Throws<ArgumentException>(() => store.Reapprove(second, 1, Path2, [], Id(1)));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Reapproval_creates_a_new_generation_and_never_inherits_the_permission()
    {
        var (t, store, _) = Create();
        using var _t = t;
        var id = store.Register(Path1, "One", ["old"], Id(1));
        store.SetAllowed(id, 1, true);

        store.Reapprove(id, 1, Path1.ToUpperInvariant(), ["new"], Id(3));
        var row = Assert.Single(store.ListAll());
        Assert.Equal((false, 2L, Id(3)), (row.Allowed, row.App!.Generation, row.App.Identity));
        Assert.Equal(["new"], row.App.FixedArgs);
        Assert.False(store.IsCurrent(id, 1));
        Assert.False(store.IsCurrent(id, 2));
        // Stale console state (old generation) cannot allow or re-approve.
        Assert.Throws<InvalidOperationException>(() => store.SetAllowed(id, 1, true));
        Assert.Throws<InvalidOperationException>(() => store.Reapprove(id, 1, Path1, [], Id(4)));
        store.SetAllowed(id, 2, true);
        Assert.True(store.IsCurrent(id, 2));
    }

    [Fact]
    public void Another_location_is_another_app()
    {
        var (t, store, _) = Create();
        using var _t = t;
        var id = store.Register(Path1, "One", [], Id(1));
        Assert.Throws<ArgumentException>(() => store.Reapprove(id, 1, Path2, [], Id(2)));
    }

    [Theory]
    [InlineData("", Path1)]
    [InlineData(@"C:\evil", Path1)]
    [InlineData("ok", @"\\server\share\app.exe")]
    [InlineData("ok", @"C:\Windows\System32\cmd.exe")]
    [InlineData("ok", @"C:\apps\run.bat")]
    public void Invalid_names_and_paths_are_refused(string name, string path)
    {
        var (t, store, _) = Create();
        using var _t = t;
        Assert.Throws<ArgumentException>(() => store.Register(path, name, [], Id(1)));
        Assert.Empty(store.ListAll());
    }

    [Fact]
    public void Invalid_arguments_and_identities_are_refused()
    {
        var (t, store, _) = Create();
        using var _t = t;
        Assert.Throws<ArgumentException>(() => store.Register(Path1, "One", ["a\nb"], Id(1)));
        Assert.Throws<ArgumentException>(() => store.Register(Path1, "One", Enumerable.Repeat("a", 17).ToArray(), Id(1)));
        Assert.Throws<ArgumentException>(() => store.Register(Path1, "One", [], Id(1) with { Sha256 = "" }));
        Assert.Throws<ArgumentException>(() => store.Register(Path1, "One", [], Id(1) with { FileId = "xyz" }));
        Assert.Empty(store.ListAll());
    }

    [Theory]
    [InlineData("fixed_args = 'not json'")]
    [InlineData("fixed_args = '[1,2]'")]
    [InlineData("fixed_args = '[null]'")]
    [InlineData("fixed_args = NULL")]
    [InlineData("sha256 = 'short'")]
    [InlineData("file_id = NULL")]
    [InlineData("launch_target = 'C:\\Windows\\System32\\cmd.exe'")]
    [InlineData("launch_target = 'relative.exe'")]
    [InlineData("kind = 'future_kind'")]
    [InlineData("generation = 0")]
    [InlineData("id = 'custom-id'")]
    public void Corrupt_rows_are_never_resolvable_or_repaired(string corruption)
    {
        var (t, store, _) = Create();
        using var _t = t;
        var id = store.Register(Path1, "One", [], Id(1));
        store.SetAllowed(id, 1, true);
        using (var c = t.Database.Open())
        {
            SysactaDatabase.Execute(c, $"UPDATE registered_apps SET {corruption}");
        }

        Assert.Empty(store.ListAllowed());
        Assert.Null(Assert.Single(store.ListAll()).App);
    }

    [Fact]
    public void Deleted_apps_are_gone_immediately_and_ids_are_not_reused()
    {
        var (t, store, _) = Create();
        using var _t = t;
        var id = store.Register(Path1, "One", [], Id(1));
        store.SetAllowed(id, 1, true);
        store.Delete(id);
        Assert.Empty(store.ListAllowed());
        Assert.False(store.IsCurrent(id, 1));
        Assert.NotEqual(id, store.Register(Path1, "One", [], Id(1)));
    }

    [Fact]
    public async Task Writes_wait_for_the_authorization_gate()
    {
        var (t, store, gate) = Create();
        using var _t = t;
        var id = store.Register(Path1, "One", [], Id(1));
        Task write;
        using (gate.BeginUpdate())
        {
            write = Task.Run(() => store.SetAllowed(id, 1, true), TestContext.Current.CancellationToken);
            Thread.Sleep(150);
            Assert.False(write.IsCompleted);
            Assert.False(store.IsCurrent(id, 1));
        }

        await write.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(store.IsCurrent(id, 1));
    }

    [Fact]
    public void Registration_is_limited()
    {
        var (t, store, _) = Create();
        using var _t = t;
        for (var i = 1; i <= AppRules.MaxApps; i++)
        {
            store.Register($@"C:\Apps\a{i}\a{i}.exe", $"A{i}", [], Id(i));
        }

        Assert.Throws<InvalidOperationException>(() => store.Register(Path1, "One", [], Id(9999)));
    }
}
