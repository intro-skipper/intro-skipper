// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Subtitles;

namespace IntroSkipper.Evaluation;

/// <summary>
/// The per-episode signal inputs that the tiered recap pipeline (<see cref="RecapTierPipeline"/>)
/// consumes: the cheap-to-read facts each detection tier needs (chapter markers, subtitle cues, a
/// shared "previously on" sting, black-frame structure, and the detected introduction). In a real
/// run these come from container metadata + ffmpeg/ffprobe; here they are authored synthetically so
/// the detection logic can be measured without media. This is intentionally NOT the ground truth —
/// the truth lives in the paired <see cref="RecapLabel"/>.
/// </summary>
internal sealed class RecapEpisodeInputs
{
    /// <summary>
    /// Gets or sets the episode duration in seconds.
    /// </summary>
    public double Duration { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a valid introduction segment was detected. When true,
    /// the sting tier's scan window is clamped to <see cref="IntroStart"/> (as in production).
    /// </summary>
    public bool IntroDetected { get; set; }

    /// <summary>
    /// Gets or sets the detected introduction start in seconds (used only when <see cref="IntroDetected"/> is true).
    /// </summary>
    public double IntroStart { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a recap chapter marker (regex/SponsorBlock) is present.
    /// </summary>
    public bool HasChapterRecap { get; set; }

    /// <summary>
    /// Gets or sets the chapter marker start in seconds (used only when <see cref="HasChapterRecap"/> is true).
    /// </summary>
    public double ChapterRecapStart { get; set; }

    /// <summary>
    /// Gets or sets the chapter marker end in seconds (used only when <see cref="HasChapterRecap"/> is true).
    /// </summary>
    public double ChapterRecapEnd { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a shared "previously on" audio sting was found across
    /// episodes (the Chromaprint signal). For a no-recap episode this is set true to model a recurring
    /// theme/ident that the detector must NOT mistake for a recap.
    /// </summary>
    public bool StingPresent { get; set; }

    /// <summary>
    /// Gets or sets the shared sting start in seconds (used only when <see cref="StingPresent"/> is true).
    /// </summary>
    public double StingStart { get; set; }

    /// <summary>
    /// Gets or sets the shared sting end in seconds (used only when <see cref="StingPresent"/> is true).
    /// </summary>
    public double StingEnd { get; set; }

    /// <summary>
    /// Gets the black-frame/fade timestamps in seconds within the opening of the episode.
    /// </summary>
    public List<double> BlackFrameTimes { get; } = [];

    /// <summary>
    /// Gets the text subtitle cues for the opening of the episode (empty when the episode has only
    /// image subtitles or no subtitles, which forces the subtitle tier to abstain).
    /// </summary>
    public List<SubtitleCue> SubtitleCues { get; } = [];
}
