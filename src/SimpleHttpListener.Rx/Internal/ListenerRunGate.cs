namespace SimpleHttpListener.Rx.Internal;

/// <summary>
/// Hands one listener or socket between the subscriptions that share it, so that a new
/// accept or receive loop never collides with the previous one.
/// </summary>
/// <remarks>
/// Rx tears a subscription down asynchronously: disposing the last subscriber of a
/// ref-counted listener cancels its loop, but the loop is still unwinding while an immediate
/// resubscription starts a new one. Without a hand-over the old loop stops the listener that
/// the new one just started, and the new subscription dies on a cancelled accept. Each
/// subscription therefore claims a generation — only the newest generation may release the
/// listener — and publishes a completion that a following run can wait for.
/// </remarks>
internal sealed class ListenerRunGate
{
    private readonly Lock _gate = new();

    private Task _previousRun = Task.CompletedTask;
    private int _currentGeneration;

    /// <summary>Takes the listener over for a new subscription.</summary>
    public Run Claim()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            var previous = _previousRun;
            _previousRun = completion.Task;

            return new Run(this, ++_currentGeneration, previous, completion);
        }
    }

    private void Release(int generation, Action release)
    {
        lock (_gate)
        {
            // A newer subscription has taken the listener over; it is no longer ours to stop.
            if (_currentGeneration == generation)
            {
                release();
            }
        }
    }

    internal sealed class Run(
        ListenerRunGate gate,
        int generation,
        Task previous,
        TaskCompletionSource completion) : IDisposable
    {
        /// <summary>Completes once the run before this one has released the listener.</summary>
        public Task Previous => previous;

        /// <summary>
        /// Releases the listener (typically stopping it), unless a newer subscription has
        /// already claimed it — in which case this run must leave it alone.
        /// </summary>
        public void Release(Action release) => gate.Release(generation, release);

        /// <summary>Signals that this run is over, for anything waiting to take over.</summary>
        public void Dispose() => completion.TrySetResult();
    }
}
