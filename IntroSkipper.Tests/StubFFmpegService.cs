// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;

/// <summary>
/// <see cref="IFFmpegService"/> stand-in for analyzer tests. Every member counts its calls,
/// honors cancellation, then runs the matching delegate hook; a member without a hook throws
/// <see cref="NotSupportedException"/> so a test fails loudly when an analyzer reaches an
/// operation it was not expected to use. Members are virtual for the rare case a hook is not
/// enough.
/// </summary>
internal class StubFFmpegService : IFFmpegService
{
    private int _versionCheckCalls;
    private int _rangeScanCalls;
    private int _creditsScanCalls;
    private int _visualScanCalls;
    private int _intervalScanCalls;

    public Func<bool>? VersionCheck { get; init; }

    public Func<QueuedEpisode, TimeRange, int, int, AnalysisMode, BlackFrame[]>? RangeBlackFrames { get; init; }

    public Func<QueuedEpisode, int, BlackFrame[]>? CreditsBlackFrames { get; init; }

    public Func<QueuedEpisode, KeyframeVisual[]>? KeyframeVisuals { get; init; }

    public Func<QueuedEpisode, TimeRange, int, int, BlackInterval[]>? BlackIntervals { get; init; }

    public int VersionCheckCalls => Volatile.Read(ref _versionCheckCalls);

    public int RangeScanCalls => Volatile.Read(ref _rangeScanCalls);

    public int CreditsScanCalls => Volatile.Read(ref _creditsScanCalls);

    public int VisualScanCalls => Volatile.Read(ref _visualScanCalls);

    public int IntervalScanCalls => Volatile.Read(ref _intervalScanCalls);

    /// <summary>Gets the arguments of the most recent range black-frame scan.</summary>
    public (TimeRange Range, int Minimum, int Threshold, AnalysisMode Mode)? LastRangeScan { get; private set; }

    /// <summary>Gets the range of the most recent blackdetect interval scan.</summary>
    public TimeRange? LastIntervalRange { get; private set; }

    public virtual Task<bool> CheckFFmpegVersionAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _versionCheckCalls);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Hook(VersionCheck)());
    }

    public virtual Task<uint[]> FingerprintAsync(QueuedEpisode episode, AnalysisMode mode, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public virtual Task<TimeRange[]> DetectSilenceAsync(QueuedEpisode episode, TimeRange range, AnalysisMode mode, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public virtual Task<BlackFrame[]> DetectBlackFramesAsync(
        QueuedEpisode episode,
        TimeRange range,
        int minimum,
        int threshold,
        AnalysisMode mode,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _rangeScanCalls);
        LastRangeScan = (range, minimum, threshold, mode);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Hook(RangeBlackFrames)(episode, range, minimum, threshold, mode));
    }

    public virtual Task<BlackFrame[]> DetectBlackFramesAsync(QueuedEpisode episode, int threshold, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _creditsScanCalls);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Hook(CreditsBlackFrames)(episode, threshold));
    }

    public virtual Task<KeyframeVisual[]> DetectKeyframeVisualsAsync(QueuedEpisode episode, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _visualScanCalls);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Hook(KeyframeVisuals)(episode));
    }

    public virtual Task<BlackInterval[]> DetectBlackIntervalsAsync(QueuedEpisode episode, TimeRange range, int threshold, int minimum, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _intervalScanCalls);
        LastIntervalRange = range;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Hook(BlackIntervals)(episode, range, threshold, minimum));
    }

    public virtual Task<double[]> DetectKeyFramesAsync(QueuedEpisode episode, TimeRange range, AnalysisMode mode, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public virtual Task<double?> ProbeAudioDurationAsync(string filePath, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public virtual FFmpegCheckResult GetCheckResult() => FFmpegCheckResult.NotRun;

    private static T Hook<T>(T? hook)
        where T : Delegate
        => hook ?? throw new NotSupportedException("The test did not configure this ffmpeg operation.");
}
