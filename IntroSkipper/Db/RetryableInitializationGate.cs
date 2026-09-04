// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Db;

/// <summary>
/// Coordinates a shared lazy initialization attempt and atomically replaces it after
/// failure. Callers that captured the same attempt continue to observe the same result.
/// </summary>
internal sealed class RetryableInitializationGate
{
    private readonly Func<Task> _attemptFactory;
    private readonly Lock _syncRoot = new();
    private Lazy<Task> _current;

    /// <summary>
    /// Initializes a new instance of the <see cref="RetryableInitializationGate"/> class.
    /// </summary>
    /// <param name="attemptFactory">Starts one initialization attempt. It runs under the
    /// attempt's lock, so a factory that does its work inline blocks concurrent first
    /// callers until it returns; a factory that dispatches to the thread pool lets them
    /// await instead.</param>
    public RetryableInitializationGate(Func<Task> attemptFactory)
    {
        ArgumentNullException.ThrowIfNull(attemptFactory);

        _attemptFactory = attemptFactory;
        _current = CreateAttempt();
    }

    /// <summary>
    /// Awaits the current attempt, resetting the gate on failure so the next caller
    /// retries. The task is awaited inside the guarded region, so a fault of the
    /// asynchronous initialization (not just of the factory) also resets the gate.
    /// <paramref name="onFirstFailure"/> runs only for the caller that installed the
    /// replacement, so a shared failure is reported exactly once.
    /// </summary>
    /// <param name="onFirstFailure">Invoked once per failed attempt with its exception.</param>
    /// <returns>A task that completes when initialization has completed.</returns>
    public async Task AwaitValueAsync(Action<Exception> onFirstFailure)
    {
        ArgumentNullException.ThrowIfNull(onFirstFailure);

        var attempt = GetAttempt();

        try
        {
            await attempt.Value.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (ResetIfCurrent(attempt))
            {
                onFirstFailure(ex);
            }

            throw;
        }
    }

    /// <summary>
    /// Returns the current shared initialization attempt.
    /// </summary>
    /// <returns>The current lazy attempt.</returns>
    public Lazy<Task> GetAttempt()
    {
        lock (_syncRoot)
        {
            return _current;
        }
    }

    /// <summary>
    /// Replaces a failed attempt when it is still current.
    /// </summary>
    /// <param name="failedAttempt">The failed attempt observed by the caller.</param>
    /// <returns><see langword="true"/> when this caller installed the replacement.</returns>
    public bool ResetIfCurrent(Lazy<Task> failedAttempt)
    {
        lock (_syncRoot)
        {
            if (!ReferenceEquals(_current, failedAttempt))
            {
                return false;
            }

            _current = CreateAttempt();
            return true;
        }
    }

    private Lazy<Task> CreateAttempt()
        => new(_attemptFactory, LazyThreadSafetyMode.ExecutionAndPublication);
}
