// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;

/// <summary>
/// <see cref="IProgress{T}"/> that keeps every reported value, in order.
/// </summary>
internal sealed class RecordingProgress : IProgress<double>
{
    private readonly List<double> _values = [];

    public IReadOnlyList<double> Values
    {
        get
        {
            lock (_values)
            {
                return [.. _values];
            }
        }
    }

    /// <summary>Gets the most recent value, or null when nothing was reported.</summary>
    public double? Value
    {
        get
        {
            lock (_values)
            {
                return _values.Count > 0 ? _values[^1] : null;
            }
        }
    }

    public void Report(double value)
    {
        lock (_values)
        {
            _values.Add(value);
        }
    }
}
