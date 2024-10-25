// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

namespace ConfusedPolarBear.Plugin.IntroSkipper.Data
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
        public Intro Introduction { get; set; } = new Intro();

        /// <summary>
        /// Gets or sets Credits.
        /// </summary>
        public Intro Credits { get; set; } = new Intro();
    }
}
