using AvaDM.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AvaDM.Core.Tests;

public sealed class DownloadSchedulerTests
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Arm_FiresOnDueCallback_OnceTimeIsAdvancedPastDue()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var fired = new List<Guid>();
        var scheduler = new DownloadScheduler(id => { fired.Add(id); return Task.CompletedTask; }, timeProvider);
        var id = Guid.NewGuid();
        var due = timeProvider.GetUtcNow().UtcDateTime.AddMinutes(5);

        scheduler.Arm(id, due);
        await Task.Delay(50); // let Arm's loop actually start awaiting the delay before checking
        Assert.Empty(fired);

        timeProvider.Advance(TimeSpan.FromMinutes(5));

        await PollUntilAsync(() => fired.Count > 0);
        Assert.Equal([id], fired);
    }

    [Fact]
    public async Task Arm_PartialAdvance_DoesNotFireBeforeDue()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var fired = false;
        var scheduler = new DownloadScheduler(_ => { fired = true; return Task.CompletedTask; }, timeProvider);
        var id = Guid.NewGuid();
        var due = timeProvider.GetUtcNow().UtcDateTime.AddMinutes(5);

        scheduler.Arm(id, due);
        await Task.Delay(50);

        timeProvider.Advance(TimeSpan.FromMinutes(4));
        await Task.Delay(200); // give a wrongly-early-firing implementation a chance to prove itself

        Assert.False(fired);
    }

    [Fact]
    public async Task Arm_DueTimeAlreadyInThePast_FiresImmediatelyWithNoAdvanceNeeded()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var fired = new List<Guid>();
        var scheduler = new DownloadScheduler(id => { fired.Add(id); return Task.CompletedTask; }, timeProvider);
        var id = Guid.NewGuid();
        var pastDue = timeProvider.GetUtcNow().UtcDateTime.AddMinutes(-5);

        scheduler.Arm(id, pastDue);

        await PollUntilAsync(() => fired.Count > 0);
        Assert.Equal([id], fired);
    }

    /// <summary>Cancelling mid-wait must make the delay throw immediately rather than letting the
    /// loop wake up, recheck, and potentially fire anyway - see DownloadScheduler.Cancel's doc
    /// comment. Advances well past the original due time after cancelling to prove the callback
    /// really never fires, rather than merely firing late.</summary>
    [Fact]
    public async Task Cancel_MidWait_StopsTheLoopAndNeverFires()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var fired = false;
        var scheduler = new DownloadScheduler(_ => { fired = true; return Task.CompletedTask; }, timeProvider);
        var id = Guid.NewGuid();
        var due = timeProvider.GetUtcNow().UtcDateTime.AddMinutes(5);

        scheduler.Arm(id, due);
        await Task.Delay(50);
        scheduler.Cancel(id);

        timeProvider.Advance(TimeSpan.FromMinutes(10));
        await Task.Delay(200);

        Assert.False(fired);
    }

    [Fact]
    public void Cancel_UnknownId_IsANoOp()
    {
        var scheduler = new DownloadScheduler(_ => Task.CompletedTask);
        scheduler.Cancel(Guid.NewGuid()); // must not throw
    }

    private static async Task PollUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.Add(PollTimeout);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(PollInterval);
    }
}
