// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Services;

/// <summary>
/// Indicates that an unstarted library request of the same kind is already queued.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="LibraryRequestAlreadyQueuedException"/> class.
/// </remarks>
/// <param name="message">The exception message.</param>
internal sealed class LibraryRequestAlreadyQueuedException(string message) : InvalidOperationException(message);
