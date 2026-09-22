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
    internal static KeyframeVisual Card(double time, double saturation = 30) => new(time, 16, 128, 128, 235, 0, saturation);

    /// <summary>A white card with black text.</summary>
    internal static KeyframeVisual WhiteCard(double time) => new(time, 0, 235, 235, 235, 0, 0);

    /// <summary>A completely white screen with no text.</summary>
    internal static KeyframeVisual WhiteScreen(double time) => new(time, 235, 235, 235, 235, 0, 0);

    /// <summary>A black roll page with white text.</summary>
    internal static KeyframeVisual Black(double time) => new(time, 16, 16, 16, 235, 0, 0);

    /// <summary>A black page with nothing on it.</summary>
    internal static KeyframeVisual BlankBlack(double time) => new(time, 16, 16, 16, 16, 0, 0);

    /// <summary>Busy content: luma spread across the range.</summary>
    internal static KeyframeVisual Content(double time) => new(time, 16, 60, 200, 235, 0, 108);

    /// <summary>A dark detailed scene: black to the blackframe filter, luma spread wide within the dark range.</summary>
    internal static KeyframeVisual Dark(double time) => new(time, 0, 10, 50, 120, 0, 0);

    /// <summary>A dark tinted scene, such as a blue night cave: black to the blackframe filter, saturated, a bright subject in front.</summary>
    internal static KeyframeVisual Tinted(double time) => new(time, 0, 10, 19, 198, 21, 30);

    /// <summary>A dark tinted scene flat enough to pass the card test on luma alone.</summary>
    internal static KeyframeVisual TintedFlat(double time) => new(time, 0, 12, 19, 198, 21, 30);

    /// <summary>A dark grey scene before the cut to the roll: black to the blackframe filter, unsaturated, its darkest tenth five levels above black.</summary>
    internal static KeyframeVisual DarkGrey(double time) => new(time, 15, 21, 26, 131, 0, 0);

    /// <summary>A black roll page on a source with lifted blacks: the darkest tenth at 20 rather than 16.</summary>
    internal static KeyframeVisual LiftedBlack(double time) => new(time, 18, 20, 20, 235, 0, 0);

    /// <summary>A dark scene behind letterbox bars: the bars pin the darkest tenth at black, the picture sits above it, a lit face on top.</summary>
    internal static KeyframeVisual LetterboxedDark(double time) => new(time, 16, 16, 32, 200, 1, 3);

    /// <summary>A black roll page with large lettering: the 90th percentile lands on the text.</summary>
    internal static KeyframeVisual BigText(double time) => new(time, 16, 16, 100, 240, 0, 0);

    /// <summary>Dense white lettering on black: too much text for the card test, still a text page.</summary>
    internal static KeyframeVisual DenseText(double time) => new(time, 16, 16, 74, 244, 0, 0);

    /// <summary>Red lettering on black: the mean saturation rises, the background's stays at zero.</summary>
    internal static KeyframeVisual RedText(double time) => new(time, 16, 16, 17, 86, 0, 11);

    /// <summary>Dense red lettering on black: dim and too dense for the flat branch, a tenth of the frame on the text.</summary>
    internal static KeyframeVisual DenseRedText(double time) => new(time, 16, 16, 81, 81, 0, 11);

    /// <summary>The first keyframe of a dark scene after a cut: black to the blackframe filter, one lit spot, no lettering.</summary>
    internal static KeyframeVisual DarkHighlight(double time) => new(time, 9, 9, 19, 104, 1, 2);

    /// <summary>A flat colour background with a subject in front: the 90th percentile leaves the background.</summary>
    internal static KeyframeVisual FlatWithSubject(double time) => new(time, 54, 126, 226, 231, 0, 45);
}
