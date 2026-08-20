using AvaDM.Core;
using Xunit;

namespace AvaDM.Core.Tests;

public sealed class SpeedTrackerTests
{
    [Fact]
    public void AddSample_FirstSample_ReturnsNull()
    {
        var tracker = new SpeedTracker();

        var speed = tracker.AddSample(1000);

        Assert.Null(speed);
    }

    [Fact]
    public async Task AddSample_TwoSamples_ReturnsRateBetweenThem()
    {
        var tracker = new SpeedTracker(TimeSpan.FromSeconds(3));
        tracker.AddSample(0);

        await Task.Delay(200);
        var speed = tracker.AddSample(100_000);

        Assert.NotNull(speed);
        // ~500,000 B/s expected; generous tolerance for scheduler jitter in CI.
        Assert.InRange(speed!.Value, 200_000, 1_000_000);
    }

    [Fact]
    public async Task AddSample_AfterWindowElapses_ForgetsSamplesOlderThanWindow()
    {
        // A short window so the test doesn't need to sleep for the real 3-second default.
        var tracker = new SpeedTracker(TimeSpan.FromMilliseconds(50));

        tracker.AddSample(0);
        await Task.Delay(20);
        tracker.AddSample(1_000); // fast burst, still inside the window

        await Task.Delay(100); // let the burst sample age out of the window
        var speed = tracker.AddSample(1_000); // no new bytes since the burst

        // Once the burst sample has aged out, the only remaining sample in-window is this call
        // itself, so there's nothing to compute a rate from - unlike a lifetime average, which
        // would keep reporting a rate for a download that has actually stalled.
        Assert.Null(speed);
    }
}
