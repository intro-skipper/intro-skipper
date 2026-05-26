// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;

namespace IntroSkipper.Tests;

internal sealed class FailingMediaDetectionService : IMediaDetectionService
{
    private readonly Exception? _fingerprintException;
    private readonly Exception? _keyframeException;

    public FailingMediaDetectionService(Exception? fingerprintException = null, Exception? keyframeException = null)
    {
        _fingerprintException = fingerprintException;
        _keyframeException = keyframeException;
    }

    public Task<uint[]> FingerprintAsync(QueuedEpisode episode, AnalysisMode mode, CancellationToken cancellationToken = default)
        => throw _fingerprintException ?? CreateException();

    public Task<double?> ProbeAudioDurationAsync(string filePath, CancellationToken cancellationToken = default)
        => throw CreateException();
    public Task<TimeRange[]> DetectSilenceAsync(QueuedEpisode episode, TimeRange range, AnalysisMode mode, CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<BlackFrame[]> DetectBlackFramesInRangeAsync(
        QueuedEpisode episode,
        TimeRange range,
        int minimum,
        int threshold,
        AnalysisMode mode,
        CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<BlackFrame[]> DetectCreditBlackFramesAsync(QueuedEpisode episode, int threshold, CancellationToken cancellationToken = default)
        => throw CreateException();

    public Task<double[]> DetectKeyFramesAsync(QueuedEpisode episode, TimeRange range, AnalysisMode mode, CancellationToken cancellationToken = default)
        => throw _keyframeException ?? CreateException();

    private static InvalidOperationException CreateException()
        => new("This test helper was created without a media detection service. Provide a test double for paths that call media detection.");
}
