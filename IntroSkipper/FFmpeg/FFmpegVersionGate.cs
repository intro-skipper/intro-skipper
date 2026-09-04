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
/// Not <c>RetryableInitializationGate</c>: that gate replaces an attempt only when it throws,
/// but an incompatible ffmpeg is an expected, re-queryable state, so a false verdict must also
/// reset the gate. A probe that outruns <paramref name="timeout"/> is treated as a false verdict
/// (callers such as <c>Entrypoint.StartAsync</c> await the check unguarded); the abandoned probe
/// finishes in the background into a discarded task and cannot publish anything, so the check
/// result and the dashboard warning always come from the attempt the waiters observed.
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

    // Published under _lock, read concurrently by the support bundle endpoint.
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
        // WaitAsync bounds the attempt even when the probe ignores its token.
        using var lifetime = new CancellationTokenSource(_timeout);
        try
        {
            var (valid, checkResult) = await _probe(lifetime.Token).WaitAsync(lifetime.Token).ConfigureAwait(false);

            lock (_lock)
            {
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
