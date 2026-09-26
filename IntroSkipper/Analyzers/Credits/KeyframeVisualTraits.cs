// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Analyzers.Credits;

/// <summary>
/// What one keyframe's visual shows on its own: tinted, lettered, card-like and solid white. Solid
/// white is neither lettered nor card-like. The other three vary independently: a roll page is
/// lettered and card-like, dense text is lettered and not card-like, and a blank page is neither.
/// Black is not among them. It is the black-frame row's percentage against a threshold the
/// black-frame rules normalize over the scan.
/// </summary>
internal static class KeyframeVisualTraits
{
    // Something is drawn on the background, text usually, at least this far from it in luma. A fade
    // or a bare wall has the spread but not the contrast.
    internal const double TextContrastMinimum = 60;

    // A card's background holds at least 80 percent of the pixels within a few luma levels.
    private const double BackgroundSpreadMaximum = 8;

    // A saturated uniform frame is never a card. A fade, a stylised transition or a saturated sky
    // has the spread and saturation of a saturated colour card, so admitting the card would admit
    // them too. Cards are muted or neutral.
    private const double SaturationCreditMaximum = 96.0;
    private const double LimitedRangeWhite = 235.0;

    // A black keyframe's least saturated tenth is its background, and black has no saturation. At or
    // above this, a black keyframe is a dark tinted scene, not a roll or a card. Coloured lettering on
    // black leaves that tenth at zero.
    private const double BlackSaturationMaximum = 10;

    /// <summary>
    /// Whether a keyframe's background is saturated. A tinted keyframe the blackframe filter counts as
    /// black is a dark tinted scene, such as a blue night cave, not a roll or a card.
    /// </summary>
    /// <param name="visual">The keyframe's visual.</param>
    /// <returns><see langword="true"/> when the keyframe is tinted.</returns>
    internal static bool IsTinted(this KeyframeVisual visual)
        => visual.SaturationLow >= BlackSaturationMaximum;

    /// <summary>
    /// Whether a keyframe shows lettering: something far above its darkest tenth, at any density and
    /// colour. A blank page fails on contrast. A dim highlight on a dark keyframe passes, since no
    /// luma percentile tells it from antialiased coloured text; a scene of such keyframes counts as a
    /// roll, as it always has, and the keyframe analyzer asks for lettering on most of a scene's pages.
    /// </summary>
    /// <param name="visual">The keyframe's visual.</param>
    /// <returns><see langword="true"/> when the keyframe shows lettering.</returns>
    internal static bool IsLettered(this KeyframeVisual visual)
        => visual.LumaMax - visual.LumaLow >= TextContrastMinimum;

    /// <summary>
    /// Whether a keyframe looks like a credit card: a dominant near-uniform background, something
    /// drawn on it far from the background in luma, and low saturation (not a vivid colour scene).
    /// Text on black, white, grey or a muted colour card looks like that. Busy content and a flat
    /// background with a subject in front never do.
    /// </summary>
    /// <param name="visual">The keyframe's visual.</param>
    /// <returns><see langword="true"/> when the keyframe looks like a credit card.</returns>
    internal static bool IsCardLike(this KeyframeVisual visual)
        => !visual.IsSolidWhite() &&
           visual.LumaHigh - visual.LumaLow <= BackgroundSpreadMaximum &&
           Math.Max(visual.LumaMax - visual.LumaHigh, visual.LumaLow - visual.LumaMin) >= TextContrastMinimum &&
           visual.Saturation < SaturationCreditMaximum;

    /// <summary>
    /// Whether a keyframe is a blank white screen, every luma percentile at limited-range white. It has
    /// no lettering, and <see cref="IsCardLike"/> keeps it out explicitly so a future change to the
    /// contrast thresholds cannot turn a solid frame into a card.
    /// </summary>
    /// <param name="visual">The keyframe's visual.</param>
    /// <returns><see langword="true"/> when the keyframe is solid white.</returns>
    internal static bool IsSolidWhite(this KeyframeVisual visual)
        => visual.LumaMin >= LimitedRangeWhite &&
           visual.LumaLow >= LimitedRangeWhite &&
           visual.LumaHigh >= LimitedRangeWhite &&
           visual.LumaMax >= LimitedRangeWhite;
}
