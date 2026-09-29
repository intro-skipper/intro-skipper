// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Every frame of a decoded window as its luma plane at a small width, with the decoder's time of
/// each frame.
/// </summary>
/// <remarks>
/// The values stay on the source's own 8-bit scale, 16 to 235 on a limited-range source and 0 to
/// 255 on a full-range one, as the keyframe scan's visuals read the same frames, so a level taken
/// from that scan compares directly. Frames sit back to back in one buffer.
/// </remarks>
public sealed class LumaWindow
{
    private readonly ReadOnlyMemory<byte> _pixels;
    private readonly double[] _times;

    /// <summary>
    /// Initializes a new instance of the <see cref="LumaWindow"/> class.
    /// </summary>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="pixels">The luma of every frame, row by row, one frame after another.</param>
    /// <param name="times">The decoder's time of each frame in seconds of media time, ascending.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    /// <exception cref="ArgumentException">The pixel count is not the frame size times the frame count.</exception>
    public LumaWindow(int width, int height, ReadOnlyMemory<byte> pixels, double[] times)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(times);
        if (pixels.Length != (long)width * height * times.Length)
        {
            throw new ArgumentException("The pixel count is not the frame size times the frame count.", nameof(pixels));
        }

        Width = width;
        Height = height;
        _pixels = pixels;
        _times = times;
    }

    /// <summary>
    /// Gets the frame width in pixels.
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// Gets the frame height in pixels.
    /// </summary>
    public int Height { get; }

    /// <summary>
    /// Gets the number of frames.
    /// </summary>
    public int FrameCount => _times.Length;

    /// <summary>
    /// Gets the decoder's time of each frame in seconds of media time, ascending.
    /// </summary>
    public IReadOnlyList<double> Times => _times;

    /// <summary>
    /// Gets one frame's luma, row by row.
    /// </summary>
    /// <param name="index">The frame index.</param>
    /// <returns>The frame's <see cref="Width"/> times <see cref="Height"/> luma values.</returns>
    public ReadOnlySpan<byte> Frame(int index) => _pixels.Span.Slice(index * Width * Height, Width * Height);
}
