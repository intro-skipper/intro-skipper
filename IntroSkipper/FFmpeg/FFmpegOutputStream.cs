// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics.CodeAnalysis;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Selects which FFmpeg output stream to capture.
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Task 2 public contract explicitly uses FFmpegOutputStream.")]
public enum FFmpegOutputStream
{
    /// <summary>
    /// Capture standard output (binary data such as Chromaprint fingerprints).
    /// </summary>
    Stdout,

    /// <summary>
    /// Capture standard error (text diagnostics such as filter output).
    /// </summary>
    Stderr,
}
