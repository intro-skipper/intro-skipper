// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Analyzers.Credits;

/// <summary>
/// One episode's keyframe scan as the black-frame rules read it: the pages in scan order and the
/// black minimum normalized over them. It answers whether a page is black and which black pages a
/// scene holds.
/// </summary>
/// <remarks>
/// Every time is relative to the credits fingerprint start, as the pages' are. A scene locates its
/// first and last black pages by scan position (see <see cref="CreditScene"/>), never by ffmpeg's
/// frame counter. The scan reads no visual: the rules on visuals are the keyframe analyzer's.
/// </remarks>
/// <param name="pages">The pages, in scan order.</param>
/// <param name="blackMinimum">The black percentage at or above which a page is black.</param>
internal sealed class KeyframeScan(IReadOnlyList<KeyframePage> pages, int blackMinimum)
{
    // A page within this many seconds of a scene's bounds belongs to the scene: an interval-supported
    // scene starts on blackdetect's clock, and its cut keyframe can sit just before that start on the
    // scan's clock.
    private const double ScenePad = 0.01;

    /// <summary>Gets the pages, in scan order, each with its black-frame row and its visual when it has one.</summary>
    internal IReadOnlyList<KeyframePage> Pages { get; } = pages;

    /// <summary>
    /// Gets the black percentage at or above which a page is black. Blackdetect takes it as pic_th,
    /// so interval confirmation uses the same definition of black.
    /// </summary>
    internal int BlackMinimum { get; } = blackMinimum;

    /// <summary>
    /// Determines whether a page at the time belongs to the range from start to end, padded by 10 ms
    /// on both sides. The card kinds test accepted and rejected ranges with it, and
    /// <see cref="BlackPages"/> is the same test for a scene's black pages.
    /// </summary>
    /// <param name="start">The range's start.</param>
    /// <param name="end">The range's end.</param>
    /// <param name="time">The page's black-frame time.</param>
    /// <returns><see langword="true"/> if the page belongs to the range; otherwise, <see langword="false"/>.</returns>
    internal static bool InScene(double start, double end, double time)
        => time >= start - ScenePad && time <= end + ScenePad;

    /// <summary>Determines whether the page at a scan position is black: its raw percentage at or above <see cref="BlackMinimum"/>.</summary>
    /// <param name="index">The page's scan position.</param>
    /// <returns><see langword="true"/> if the page is black; otherwise, <see langword="false"/>.</returns>
    internal bool IsBlack(int index) => Pages[index].Frame.Percentage >= BlackMinimum;

    /// <summary>
    /// Enumerates a scene's black pages: every black page whose time is <see cref="InScene"/> of the
    /// scene's bounds, walked over the whole scan, so a page just before an interval start counts.
    /// The enumeration is lazy and walks the scan each time it is enumerated.
    /// </summary>
    /// <param name="scene">The scene, by its time bounds.</param>
    /// <returns>The black pages with their scan positions, in scan order, pages without a visual included.</returns>
    internal IEnumerable<(int Index, KeyframePage Page)> BlackPages(CreditScene scene)
    {
        for (var i = 0; i < Pages.Count; i++)
        {
            if (IsBlack(i) && InScene(scene.StartTime, scene.EndTime, Pages[i].Frame.Time))
            {
                yield return (i, Pages[i]);
            }
        }
    }
}
