// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
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
/// Two concurrent calls for one episode and the same inputs may both probe, and the first
/// result stays.
/// </para>
/// </remarks>
/// <typeparam name="TInputs">The inputs besides the episode object that the result depends on, such as the path and the settings the probe reads. Compared by value.</typeparam>
/// <typeparam name="TResult">The probe's result.</typeparam>
internal sealed class EpisodeProbeMemo<TInputs, TResult>
    where TInputs : notnull
    where TResult : class
{
    private readonly ConditionalWeakTable<QueuedEpisode, ConcurrentDictionary<TInputs, TResult>> _results = new();

    /// <summary>
    /// Returns the result kept for an episode and inputs, or runs the probe and keeps its result.
    /// </summary>
    /// <param name="episode">The episode object of the current pass.</param>
    /// <param name="inputs">The inputs the result depends on.</param>
    /// <param name="probe">Runs the probe on the token it is given and returns <see langword="null"/> when it failed.</param>
    /// <param name="cancellationToken">The token passed to <paramref name="probe"/>.</param>
    /// <returns>The kept or new result, or <see langword="null"/> when the probe failed.</returns>
    public async Task<TResult?> GetAsync(
        QueuedEpisode episode,
        TInputs inputs,
        Func<CancellationToken, Task<TResult?>> probe,
        CancellationToken cancellationToken)
    {
        var results = _results.GetOrCreateValue(episode);
        if (results.TryGetValue(inputs, out var kept))
        {
            return kept;
        }

        var result = await probe(cancellationToken).ConfigureAwait(false);
        return result is null ? null : results.GetOrAdd(inputs, result);
    }
}
