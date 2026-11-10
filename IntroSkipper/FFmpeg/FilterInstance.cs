// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Security.Cryptography;
using System.Text;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// A filter instance a scan names for itself, as in <c>showinfo@Ab3dE5gH</c>, and the reader of
/// the lines it logs.
/// </summary>
/// <remarks>
/// ffmpeg logs an instance's messages under its log context, <c>[name @ address] </c>. Its input and
/// output dumps print a file's name and tags raw, line feeds included, so a file can print a line
/// that reads like a filter's, but not under a name drawn for the scan. The context counts anywhere
/// in a line, so a message from another thread that lands in front of it changes nothing. Only a
/// filter that logs through its own instance can be named this way: blackdetect and silencedetect
/// log under their class names.
/// </remarks>
/// <param name="name">The instance name, <c>filter@token</c>. Production draws it with <see cref="Draw"/>.</param>
internal sealed class FilterInstance(string name)
{
    private const string TokenCharacters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    private readonly string _context = $"[{name} @ ";

    private readonly byte[] _contextBytes = Encoding.ASCII.GetBytes($"[{name} @ ");

    /// <summary>
    /// Gets the instance name, which goes in front of the filter's options in a filter graph, as in
    /// <c>showinfo@Ab3dE5gH=checksum=0</c>.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// Names an instance of a filter with a token drawn for one scan.
    /// </summary>
    /// <param name="filter">The filter, such as <c>showinfo</c>.</param>
    /// <returns>The instance.</returns>
    public static FilterInstance Draw(string filter) => new($"{filter}@{RandomNumberGenerator.GetString(TokenCharacters, 8)}");

    /// <summary>
    /// Reads the message of a log line.
    /// </summary>
    /// <param name="line">One line of ffmpeg's log.</param>
    /// <returns>The text after this instance's log context, or <see langword="null"/> when the line is not this instance's.</returns>
    public string? Message(string line)
    {
        var start = MessageStart(line.AsSpan(), _context, "] ");
        return start < 0 ? null : line[start..];
    }

    /// <summary>
    /// Reads the message of a log line held as bytes.
    /// </summary>
    /// <param name="line">One line of ffmpeg's log.</param>
    /// <param name="message">The bytes after this instance's log context, or empty when the line is not this instance's.</param>
    /// <returns>Whether the line is this instance's.</returns>
    public bool TryReadMessage(ReadOnlySpan<byte> line, out ReadOnlySpan<byte> message)
    {
        var start = MessageStart(line, _contextBytes, "] "u8);
        message = start < 0 ? [] : line[start..];
        return start >= 0;
    }

    // Where the message starts: after the context, the instance's address and the "] " that ends it.
    private static int MessageStart<T>(ReadOnlySpan<T> line, ReadOnlySpan<T> context, ReadOnlySpan<T> end)
        where T : IEquatable<T>
    {
        var at = line.IndexOf(context);
        if (at < 0)
        {
            return -1;
        }

        var address = at + context.Length;
        var close = line[address..].IndexOf(end);
        return close < 0 ? -1 : address + close + end.Length;
    }
}
