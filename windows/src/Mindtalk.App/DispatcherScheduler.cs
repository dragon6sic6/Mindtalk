using Microsoft.UI.Dispatching;
using Mindtalk.Core.Dictation;

namespace Mindtalk.App;

/// <summary>One-shot timers on the UI thread. Safe to call from any thread.</summary>
public sealed class DispatcherScheduler(DispatcherQueue queue) : IScheduler
{
    public DateTime Now => DateTime.UtcNow;

    public IDisposable After(TimeSpan delay, Action action)
    {
        var stopper = new Stopper();
        if (queue.HasThreadAccess) stopper.Start(queue, delay, action);
        else queue.TryEnqueue(() => stopper.Start(queue, delay, action));
        return stopper;
    }

    private sealed class Stopper : IDisposable
    {
        private DispatcherQueueTimer? _timer;
        private bool _cancelled;

        public void Start(DispatcherQueue queue, TimeSpan delay, Action action)
        {
            if (_cancelled) return;
            _timer = queue.CreateTimer();
            _timer.Interval = delay;
            _timer.IsRepeating = false;
            _timer.Tick += (t, _) => { t.Stop(); if (!_cancelled) action(); };
            _timer.Start();
        }

        public void Dispose()
        {
            _cancelled = true;
            _timer?.Stop();
        }
    }
}
