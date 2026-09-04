// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Single-flight gate around the ffmpeg version probe: a successful probe is memoized for the
/// gate's lifetime, a false verdict resets the gate so the next call probes again, and every
/// concurrent caller shares one in-flight probe.
/// </summary>
/// <remarks>
/// Deliberately not <c>RetryableInitializationGate</c>: that gate replaces an attempt only when
/// it throws, but this one must also reset on a successful probe whose verdict is false (an
/// incompatible ffmpeg is an expected, re-queryable state), keep success sticky, and publish a
/// false verdict and its reset atomically so the next caller is guaranteed a fresh probe.
/// </remarks>
/// <param name="logger">Logger.</param>
/// <param name="probe">Runs one probe. Returns the verdict and, for the real ffmpeg probe, the
/// support-bundle snapshot; test probes return <see langword="null"/> for the snapshot.</param>
/// <param name="timeout">Bounds one probe. The probe ignores caller tokens, so without this a hung
/// ffmpeg would wedge the gate until a plugin reload.</param>
internal sealed partial class FFmpegVersionGate(
    ILogger logger,
    Func<CancellationToken, Task<(bool Valid, FFmpegCheckResult? Result)>> probe,
    TimeSpan timeout)
{
    private readonly ILogger _logger = logger;
    private readonly Func<CancellationToken, Task<(bool Valid, FFmpegCheckResult? Result)>> _probe = probe;
    private readonly TimeSpan _timeout = timeout;
    private readonly Lock _lock = new();

    // Published under _lock by the attempt whose verdict the waiters observe (an abandoned
    // timed-out probe cannot write it) and read concurrently by the support bundle endpoint.
    private volatile FFmpegCheckResult _checkResult = FFmpegCheckResult.NotRun;
    private Task<bool>? _probeTask;
    private volatile bool _succeeded;

    /// <summary>
    /// Gets the outcome of the most recent probe; <see cref="FFmpegCheckResult.NotRun"/> before the first.
    /// </summary>
    public FFmpegCheckResult CheckResult => _checkResult;

    /// <summary>
    /// Returns the memoized verdict or joins (or starts) the shared probe.
    /// </summary>
    /// <param name="cancellationToken">Cancels this caller's wait only; the probe keeps running.</param>
    /// <returns><see langword="true"/> if the probe found a compatible ffmpeg; otherwise <see langword="false"/>.</returns>
    public async Task<bool> CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_succeeded)
        {
            return true;
        }

        TaskCompletionSource<bool>? completion = null;
        Task<bool> probeTask;
        lock (_lock)
        {
            if (_succeeded)
            {
                return true;
            }

            if (_probeTask is null)
            {
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _probeTask = completion.Task;
            }

            probeTask = _probeTask;
        }

        if (completion is not null)
        {
            _ = RunProbeAsync(completion);
        }

        return await probeTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunProbeAsync(TaskCompletionSource<bool> completion)
    {
        // WaitAsync keeps the gate safe even against a probe that ignores its token: the
        // attempt fails, the gate resets, the next call retries, and the abandoned probe's
        // eventual result is discarded.
        using var lifetime = new CancellationTokenSource(_timeout);
        try
        {
            var (valid, checkResult) = await _probe(lifetime.Token).WaitAsync(lifetime.Token).ConfigureAwait(false);

            lock (_lock)
            {
                // Side effects are published only by the attempt the waiters observe: an abandoned
                // probe that outran its lifetime completes into a discarded task and cannot
                // overwrite the check result or warning flag of a newer attempt.
                if (checkResult is not null)
                {
                    _checkResult = checkResult;
                    if (!valid)
                    {
                        WarningManager.SetFlag(PluginWarning.IncompatibleFFmpegBuild);
                    }
                }

                if (valid)
                {
                    _succeeded = true;
                }

                completion.SetResult(valid);
                if (!valid)
                {
                    _probeTask = null;
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // A probe that outran its lifetime is a failed attempt, not a cancellation of its
            // waiters: CheckFFmpegVersionAsync documents false on any error, and callers such as
            // Entrypoint.StartAsync await it unguarded. The abandoned probe finishes in the
            // background; an unobserved fault there is inert. This attempt is the one the
            // waiters observe, so it also publishes the verdict to the support bundle and the
            // dashboard warning instead of leaving a stale "okay" snapshot.
            LogFfmpegVersionProbeTimedOut(_logger, _timeout);

            lock (_lock)
            {
                _checkResult = new FFmpegCheckResult("timed_out", []);
                WarningManager.SetFlag(PluginWarning.IncompatibleFFmpegBuild);
                completion.SetResult(false);
                _probeTask = null;
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                completion.SetException(ex);
                _probeTask = null;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "FFmpeg version check did not finish within {Timeout}; treating the installed FFmpeg as invalid until a later check succeeds")]
    private static partial void LogFfmpegVersionProbeTimedOut(ILogger logger, TimeSpan timeout);
}
