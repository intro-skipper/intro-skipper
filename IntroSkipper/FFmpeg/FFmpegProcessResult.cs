// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics.CodeAnalysis;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Encapsulates the raw byte output from an FFmpeg process execution.
/// </summary>
/// <param name="Output">The captured stdout or stderr bytes from the FFmpeg process.</param>
/// <param name="ExitCode">The process exit code, or -1 if the process did not exit cleanly.</param>
[SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Lightweight data carrier for raw FFmpeg output; callers consume via ReadOnlySpan<byte>.")]
public readonly record struct FFmpegProcessResult(byte[] Output, int ExitCode);
