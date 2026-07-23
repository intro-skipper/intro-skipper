// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Helper;

/// <summary>
/// Shared exception classification helpers.
/// </summary>
internal static class ExceptionHelpers
{
    /// <summary>
    /// Returns whether an exception is critical and must never be swallowed by
    /// log-and-continue or report-and-continue error handling.
    /// </summary>
    /// <param name="exception">The exception to classify.</param>
    /// <returns><see langword="true"/> for process-fatal exception types.</returns>
    internal static bool IsCritical(this Exception exception)
        => exception is OutOfMemoryException
            or StackOverflowException
            or ThreadAbortException
            or ThreadInterruptedException
            or AccessViolationException;
}
