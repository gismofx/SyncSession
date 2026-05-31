using System;

namespace SyncSession.UnitTests;

/// <summary>
/// Synchronous IProgress&lt;T&gt; implementation for unit tests.
/// Unlike Progress&lt;T&gt;, callbacks fire inline on the calling thread — no thread pool scheduling,
/// no Task.Delay required to collect reports. Eliminates timing-dependent test failures
/// caused by thread pool saturation under parallel test execution.
/// </summary>
public sealed class SynchronousProgress<T> : IProgress<T>
{
    private readonly Action<T> _callback;

    public SynchronousProgress(Action<T> callback)
    {
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
    }

    public void Report(T value) => _callback(value);
}
