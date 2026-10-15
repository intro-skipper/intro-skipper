// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using IntroSkipper.Data;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Keeps one probe's results per <see cref="QueuedEpisode"/> object, so the modes of one
/// analysis pass probe a file once.
/// </summary>
/// <remarks>
/// <para>
/// <c>SeasonResolver</c> builds new <see cref="QueuedEpisode"/> objects for every pass, so a
/// result lasts one pass. The next pass probes again, and a file replaced between passes never
/// gets an old result. A result is released when its episode object is collected.
/// </para>
/// <para>
/// The memo keeps only results that are not <see langword="null"/>. A probe that returns
/// <see langword="null"/>, throws or is cancelled leaves nothing, so the next call probes again.
/// Concurrent calls for one episode and the same inputs share the in-flight probe.
/// </para>
/// </remarks>
/// <typeparam name="TInputs">The inputs besides the episode object that the result depends on, such as the path and the settings the probe reads. Compared by value.</typeparam>
/// <typeparam name="TResult">The probe's result.</typeparam>
internal sealed class EpisodeProbeMemo<TInputs, TResult>
    where TInputs : notnull
    where TResult : class
{
    private readonly ConditionalWeakTable<QueuedEpisode, ConcurrentDictionary<TInputs, Lazy<Task<TResult?>>>> _probes = new();

    /// <summary>
    /// Returns the result kept for an episode and inputs, or runs the probe and keeps its result.
    /// </summary>
    /// <param name="episode">The episode object of the current pass.</param>
    /// <param name="inputs">The inputs the result depends on.</param>
    /// <param name="probe">Runs the shared probe and returns <see langword="null"/> when it failed.</param>
    /// <param name="cancellationToken">Cancels this caller's wait without cancelling a shared probe.</param>
    /// <returns>The kept or new result, or <see langword="null"/> when the probe failed.</returns>
    public async Task<TResult?> GetAsync(
        QueuedEpisode episode,
        TInputs inputs,
        Func<CancellationToken, Task<TResult?>> probe,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var probes = _probes.GetOrCreateValue(episode);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = new Lazy<Task<TResult?>>(
                () => probe(CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication);
            var entry = probes.GetOrAdd(inputs, candidate);
            Task<TResult?> task;
            try
            {
                task = entry.Value;
            }
            catch
            {
                Remove(probes, inputs, entry);
                throw;
            }

            if (task.IsFaulted || task.IsCanceled)
            {
                Remove(probes, inputs, entry);
                continue;
            }

            _ = task.ContinueWith(
                completed => RemoveUnsuccessful(probes, inputs, entry, completed),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            try
            {
                var result = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (result is not null)
                {
                    return result;
                }

                Remove(probes, inputs, entry);
                if (probes.TryGetValue(inputs, out var replacement) && !ReferenceEquals(replacement, entry))
                {
                    continue;
                }

                return null;
            }
            catch
            {
                if (task.IsFaulted || task.IsCanceled)
                {
                    Remove(probes, inputs, entry);
                }

                throw;
            }
        }
    }

    private static void Remove(
        ConcurrentDictionary<TInputs, Lazy<Task<TResult?>>> probes,
        TInputs inputs,
        Lazy<Task<TResult?>> entry)
        => probes.TryRemove(KeyValuePair.Create(inputs, entry));

    private static void RemoveUnsuccessful(
        ConcurrentDictionary<TInputs, Lazy<Task<TResult?>>> probes,
        TInputs inputs,
        Lazy<Task<TResult?>> entry,
        Task<TResult?> probe)
    {
        if (probe.IsFaulted)
        {
            _ = probe.Exception;
        }

        if (probe.IsCanceled || (probe.Status == TaskStatus.RanToCompletion && probe.Result is null))
        {
            Remove(probes, inputs, entry);
        }
    }
}
