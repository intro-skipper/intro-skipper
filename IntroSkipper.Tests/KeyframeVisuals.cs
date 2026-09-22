// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using IntroSkipper.Data;

/// <summary>
/// Keyframe visuals for tests, on the 8-bit scale the scan reports.
/// </summary>
internal static class KeyframeVisuals
{
    /// <summary>A grey credit card with white text: uniform background, text far above it.</summary>
    internal static KeyframeVisual Card(double time, double saturation = 30) => new(time, 16, 128, 128, 235, saturation);

    /// <summary>A white card with black text.</summary>
    internal static KeyframeVisual WhiteCard(double time) => new(time, 0, 235, 235, 235, 0);

    /// <summary>A completely white screen with no text.</summary>
    internal static KeyframeVisual WhiteScreen(double time) => new(time, 235, 235, 235, 235, 0);

    /// <summary>A black roll page with white text.</summary>
    internal static KeyframeVisual Black(double time) => new(time, 16, 16, 16, 235, 0);

    /// <summary>A black page with nothing on it.</summary>
    internal static KeyframeVisual BlankBlack(double time) => new(time, 16, 16, 16, 16, 0);

    /// <summary>Busy content: luma spread across the range.</summary>
    internal static KeyframeVisual Content(double time) => new(time, 16, 60, 200, 235, 108);

    /// <summary>A dark detailed scene: black to the blackframe filter, luma spread wide within the dark range.</summary>
    internal static KeyframeVisual Dark(double time) => new(time, 0, 10, 50, 120, 0);

    /// <summary>A flat colour background with a subject in front: the 90th percentile leaves the background.</summary>
    internal static KeyframeVisual FlatWithSubject(double time) => new(time, 54, 126, 226, 231, 45);
}
