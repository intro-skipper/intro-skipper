// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;

/// <summary>
/// <see cref="IFFmpegService"/> stand-in for analyzer tests. Every member records its call,
/// honors cancellation, then runs the matching delegate hook; a member without a hook throws
/// <see cref="NotSupportedException"/> so a test fails loudly when an analyzer reaches an
/// operation it was not expected to use. Members are virtual for the rare case a hook is not
/// enough. The probes, range scans, interval scans and luma decodes, go to <see cref="Calls"/>
/// in the order they arrive, before the hook runs, so a probe whose hook throws is recorded too;
/// the other members count their calls.
/// </summary>
internal class StubFFmpegService : IFFmpegService
{
    private readonly ConcurrentQueue<Call> _calls = new();
    private int _versionCheckCalls;
    private int _creditsScanCalls;
    private int _fingerprintCalls;
    private int _visualScanCalls;
    private int _probeCalls;

    public Func<bool>? VersionCheck { get; init; }

    public Func<string, double?>? AudioDuration { get; init; }

    public Func<QueuedEpisode, AnalysisMode, uint[]>? Fingerprints { get; init; }

    public Func<QueuedEpisode, TimeRange, int, int, AnalysisMode, BlackFrame[]>? RangeBlackFrames { get; init; }

    public Func<QueuedEpisode, int, BlackFrame[]>? CreditsBlackFrames { get; init; }

    public Func<QueuedEpisode, TimeRange, AnalysisMode, TimeRange[]>? Silence { get; init; }

    public Func<QueuedEpisode, TimeRange, AnalysisMode, double[]>? KeyFrames { get; init; }

    public Func<QueuedEpisode, KeyframeVisual[]>? KeyframeVisuals { get; init; }

    public Func<QueuedEpisode, TimeRange, int, int, BlackInterval[]>? BlackIntervals { get; init; }

    public Func<QueuedEpisode, TimeRange, int, LumaWindow?>? LumaWindows { get; init; }

    public int VersionCheckCalls => Volatile.Read(ref _versionCheckCalls);

    public int CreditsScanCalls => Volatile.Read(ref _creditsScanCalls);

    public int FingerprintCalls => Volatile.Read(ref _fingerprintCalls);

    public int VisualScanCalls => Volatile.Read(ref _visualScanCalls);

    public int ProbeCalls => Volatile.Read(ref _probeCalls);

    /// <summary>Gets the probes the stub received, in order: tests assert the whole log.</summary>
    public IReadOnlyList<Call> Calls => [.. _calls];

    /// <summary>Gets the credits window start of the most recent credits black-frame scan.</summary>
    public double? LastCreditsScanStart { get; private set; }

    public virtual Task<bool> CheckFFmpegVersionAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _versionCheckCalls);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Hook(VersionCheck)());
    }

    public virtual Task<uint[]> FingerprintAsync(QueuedEpisode episode, AnalysisMode mode, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _fingerprintCalls);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Hook(Fingerprints)(episode, mode));
    }

    public virtual Task<TimeRange[]> DetectSilenceAsync(QueuedEpisode episode, TimeRange range, AnalysisMode mode, CancellationToken cancellationToken = default)
        => Task.FromResult(Hook(Silence)(episode, range, mode));

    public virtual Task<BlackFrame[]> DetectBlackFramesAsync(
        QueuedEpisode episode,
        TimeRange range,
        int minimum,
        int threshold,
        AnalysisMode mode,
        CancellationToken cancellationToken = default)
    {
        _calls.Enqueue(new RangeScan(range.Start, range.End, minimum, threshold, mode));
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Hook(RangeBlackFrames)(episode, range, minimum, threshold, mode));
    }

    public virtual Task<BlackFrame[]> DetectBlackFramesAsync(QueuedEpisode episode, int threshold, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _creditsScanCalls);
        LastCreditsScanStart = episode.CreditsFingerprintStart;
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
        _calls.Enqueue(new IntervalScan(range.Start, range.End, threshold, minimum));
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Hook(BlackIntervals)(episode, range, threshold, minimum));
    }

    public virtual Task<double[]> DetectKeyFramesAsync(QueuedEpisode episode, TimeRange range, AnalysisMode mode, CancellationToken cancellationToken = default)
        => Task.FromResult(Hook(KeyFrames)(episode, range, mode));

    public virtual Task<LumaWindow?> DecodeLumaWindowAsync(QueuedEpisode episode, TimeRange window, int width, CancellationToken cancellationToken = default)
    {
        _calls.Enqueue(new LumaDecode(window.Start, window.End, width));
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Hook(LumaWindows)(episode, window, width));
    }

    public virtual Task<double?> ProbeAudioDurationAsync(string filePath, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _probeCalls);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Hook(AudioDuration)(filePath));
    }

    public virtual FFmpegCheckResult GetCheckResult() => FFmpegCheckResult.NotRun;

    private static T Hook<T>(T? hook)
        where T : Delegate
        => hook ?? throw new NotSupportedException("The test did not configure this ffmpeg operation.");

    /// <summary>A probe the stub received. Times are copied out of the <see cref="TimeRange"/>, which compares by reference.</summary>
    internal abstract record Call;

    /// <summary>A black-frame scan over a range, as the boundary probe and the legacy analyzer run it.</summary>
    internal sealed record RangeScan(double Start, double End, int Minimum, int Threshold, AnalysisMode Mode) : Call;

    /// <summary>A blackdetect interval scan over a range.</summary>
    internal sealed record IntervalScan(double Start, double End, int Threshold, int Minimum) : Call;

    /// <summary>A luma window decode for the lead-in probe.</summary>
    internal sealed record LumaDecode(double Start, double End, int Width) : Call;
}
