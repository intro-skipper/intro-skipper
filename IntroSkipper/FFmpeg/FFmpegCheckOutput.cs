// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Raw output captured from one FFmpeg probe.
/// </summary>
/// <param name="Name">Short probe name, e.g. <c>version</c> or <c>muxer list</c>.</param>
/// <param name="Output">Standard output of the probe.</param>
public sealed record FFmpegCheckOutput(string Name, string Output);
