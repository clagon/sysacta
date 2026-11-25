using Sysacta.App.ViewModels;

namespace Sysacta.App.Tests;

public class LatestValueSaverTests
{
    [Fact]
    public async Task A_toggle_made_during_a_save_is_saved_after_it_so_the_last_value_wins()
    {
        var shown = true;
        var saved = new List<bool>();
        var firstSave = new TaskCompletionSource<bool>();
        var saver = new LatestValueSaver<bool>(() => shown, value =>
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
    public async Task A_failed_save_reloads_the_view_and_no_longer_counts_as_saving()
    {
        bool? savingDuringReload = null;
        LatestValueSaver<bool>? saver = null;
        saver = new LatestValueSaver<bool>(() => true, _ => Task.FromResult(false), () => { savingDuringReload = saver!.IsSaving; return Task.CompletedTask; });

        await saver.ChangedAsync();

        Assert.False(savingDuringReload); // otherwise a reload would skip the switch and keep showing the unsaved value
        Assert.False(saver.IsSaving);
    }

    [Fact]
    public async Task IsSaving_is_true_only_while_a_save_runs()
    {
        var pending = new TaskCompletionSource<bool>();
        var saver = new LatestValueSaver<bool>(() => true, _ => pending.Task, () => Task.CompletedTask);

        var running = saver.ChangedAsync();
        Assert.True(saver.IsSaving);
        pending.SetResult(true);
        await running;
        Assert.False(saver.IsSaving);
    }
}
