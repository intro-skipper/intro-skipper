// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using Jellyfin.MediaEncoding.Keyframes;

namespace IntroSkipper.Analyzers;

/// <summary>
/// Interface for extracting keyframe data from video files.
/// Implementations normalize timestamps from various source formats (MKV, ffprobe) to .NET ticks.
/// </summary>
public interface IKeyframeExtractor
{
    /// <summary>
    /// Extracts keyframe data from a video file.
    /// All timestamps in the returned KeyframeData are normalized to ticks (1 tick = 100 nanoseconds) regardless of source format.
    /// </summary>
    /// <param name="filePath">Path to the video file.</param>
    /// <returns>KeyframeData containing duration and keyframe timestamps, both in ticks.</returns>
    /// <exception cref="System.IO.FileNotFoundException">Thrown when the file does not exist.</exception>
    /// <exception cref="System.InvalidOperationException">Thrown when extraction fails.</exception>
    KeyframeData GetKeyframeData(string filePath);
}
