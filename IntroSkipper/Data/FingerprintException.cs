// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024 AbandonedCart
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System;

namespace IntroSkipper.Data;

/// <summary>
/// Exception raised when an error is encountered analyzing audio.
/// </summary>
public class FingerprintException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FingerprintException"/> class.
    /// </summary>
    public FingerprintException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FingerprintException"/> class.
    /// </summary>
    /// <param name="message">Exception message.</param>
    public FingerprintException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FingerprintException"/> class.
    /// </summary>
    /// <param name="message">Exception message.</param>
    /// <param name="inner">Inner exception.</param>
    public FingerprintException(string message, Exception inner) : base(message, inner)
    {
    }
}
