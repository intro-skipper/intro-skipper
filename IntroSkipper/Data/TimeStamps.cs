// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

namespace IntroSkipper.Data
{
    /// <summary>
    /// Result of fingerprinting and analyzing two episodes in a season.
    /// All times are measured in seconds relative to the beginning of the media file.
    /// </summary>
    public class TimeStamps
    {
        /// <summary>
        /// Gets or sets Introduction.
        /// </summary>
        public Segment Introduction { get; set; } = new Segment();

        /// <summary>
        /// Gets or sets Credits.
        /// </summary>
        public Segment Credits { get; set; } = new Segment();

        /// <summary>
        /// Gets or sets Recap.
        /// </summary>
        public Segment Recap { get; set; } = new Segment();

        /// <summary>
        /// Gets or sets Preview.
        /// </summary>
        public Segment Preview { get; set; } = new Segment();
    }
}
