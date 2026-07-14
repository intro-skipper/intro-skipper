// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Db;

/// <summary>
/// Coordinates a shared lazy initialization attempt and atomically replaces it after
/// failure. Callers that captured the same attempt continue to observe the same result.
/// </summary>
/// <typeparam name="T">The initialization result stored by the lazy attempt.</typeparam>
internal sealed class RetryableInitializationGate<T>
{
    private readonly Func<T> _valueFactory;
    private readonly Lock _syncRoot = new();
    private Lazy<T> _current;

    /// <summary>
    /// Initializes a new instance of the <see cref="RetryableInitializationGate{T}"/> class.
    /// </summary>
    /// <param name="valueFactory">Creates one initialization attempt.</param>
    public RetryableInitializationGate(Func<T> valueFactory)
    {
        ArgumentNullException.ThrowIfNull(valueFactory);

        _valueFactory = valueFactory;
        _current = CreateAttempt();
    }

    /// <summary>
    /// Returns the current shared initialization attempt.
    /// </summary>
    /// <returns>The current lazy attempt.</returns>
    public Lazy<T> GetAttempt()
    {
        lock (_syncRoot)
        {
            return _current;
        }
    }

    /// <summary>
    /// Replaces a failed attempt when it is still current.
    /// </summary>
    /// <param name="failedAttempt">The failed attempt observed by the caller.</param>
    /// <returns><see langword="true"/> when this caller installed the replacement.</returns>
    public bool ResetIfCurrent(Lazy<T> failedAttempt)
    {
        lock (_syncRoot)
        {
            if (!ReferenceEquals(_current, failedAttempt))
            {
                return false;
            }

            _current = CreateAttempt();
            return true;
        }
    }

    private Lazy<T> CreateAttempt()
        => new(_valueFactory, LazyThreadSafetyMode.ExecutionAndPublication);
}
