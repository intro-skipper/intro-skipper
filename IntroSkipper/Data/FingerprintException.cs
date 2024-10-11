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
