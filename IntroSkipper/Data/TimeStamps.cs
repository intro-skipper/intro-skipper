// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Generic;

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

        /// <summary>
        /// Gets the collection of Commercial segments.
        /// Multiple commercials can appear throughout a video.
        /// </summary>
        public IList<Segment> Commercials { get; } = [];

        /// <summary>
        /// Gets or sets a single Commercial segment for backward compatibility.
        /// Returns the first commercial if multiple exist, or an empty segment if none exist.
        /// When set, it replaces all existing commercials with a single segment.
        /// </summary>
        public Segment Commercial
        {
            get => Commercials.Count > 0 ? Commercials[0] : new Segment();
            set
            {
                Commercials.Clear();
                if (value.Valid)
                {
                    Commercials.Add(value);
                }
            }
        }
    }
}
