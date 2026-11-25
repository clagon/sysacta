using Sysacta.App.ViewModels;

namespace Sysacta.App.Tests;

public class ToggleSaverTests
{
    [Fact]
    public async Task A_toggle_made_during_a_save_is_saved_after_it_so_the_last_value_wins()
    {
        var shown = true;
        var saved = new List<bool>();
        var firstSave = new TaskCompletionSource<bool>();
        var saver = new ToggleSaver(() => shown, value =>
        {
            saved.Add(value);
            return saved.Count == 1 ? firstSave.Task : Task.FromResult(true);
        }, () => Task.CompletedTask);

        var running = saver.ChangedAsync();   // saves "true" (pending)
        shown = false;
        await saver.ChangedAsync();           // ignored while saving; the loop picks it up
        firstSave.SetResult(true);
        await running;

        Assert.Equal([true, false], saved);
    }

    [Fact]
    public async Task A_failed_save_reloads_the_view()
    {
        var reloaded = false;
        var saver = new ToggleSaver(() => true, _ => Task.FromResult(false), () => { reloaded = true; return Task.CompletedTask; });

        await saver.ChangedAsync();

        Assert.True(reloaded);
    }
}
