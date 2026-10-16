using Microsoft.UI.Dispatching;
using Mindtalk.Core.Dictation;

namespace Mindtalk.App;

/// <summary>One-shot timers on the UI thread.</summary>
public sealed class DispatcherScheduler(DispatcherQueue queue) : IScheduler
{
    public DateTime Now => DateTime.UtcNow;

    public IDisposable After(TimeSpan delay, Action action)
    {
        var timer = queue.CreateTimer();
        timer.Interval = delay;
        timer.IsRepeating = false;
        timer.Tick += (t, _) => { t.Stop(); action(); };
        timer.Start();
        return new Stopper(timer);
    }

    private sealed class Stopper(DispatcherQueueTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}
