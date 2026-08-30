// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Db;

/// <summary>
/// Awaitable helpers for gates whose result is an asynchronous initialization task.
/// </summary>
internal static class RetryableInitializationGateExtensions
{
    /// <summary>
    /// Awaits the current attempt's task, resetting the gate on failure so the next
    /// caller retries. <paramref name="onFirstFailure"/> runs only for the caller that
    /// installed the replacement, so a shared failure is reported exactly once.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="RetryableInitializationGate{T}.GetValue"/> with one deliberate
    /// difference: the attempt's task is awaited inside the guarded region, so a fault of
    /// the asynchronous initialization (not just of the factory) also resets the gate.
    /// Keep the two envelopes in sync.
    /// </remarks>
    /// <param name="gate">The initialization gate.</param>
    /// <param name="onFirstFailure">Invoked once per failed attempt with its exception.</param>
    /// <returns>A task that completes when initialization has completed.</returns>
    public static async Task AwaitValueAsync(this RetryableInitializationGate<Task> gate, Action<Exception> onFirstFailure)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(onFirstFailure);

        var attempt = gate.GetAttempt();

        try
        {
            await attempt.Value.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (gate.ResetIfCurrent(attempt))
            {
                onFirstFailure(ex);
            }

            throw;
        }
    }
}
