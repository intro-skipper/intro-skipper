// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;

namespace IntroSkipper.Controllers
{
    /// <summary>
    /// Contains information about a show.
    /// </summary>
    public class ShowInfos
    {
        /// <summary>
        /// Gets or sets the Name of the show.
        /// </summary>
        public required string SeriesName { get; set; }

        /// <summary>
        /// Gets or sets the Year of the show.
        /// </summary>
        public required string ProductionYear { get; set; }

        /// <summary>
        /// Gets or sets the Library of the show.
        /// </summary>
        public required string LibraryName { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether its a movie.
        /// </summary>
        public required bool IsMovie { get; set; }

        /// <summary>
        /// Gets the Seasons of the show.
        /// </summary>
        public required Dictionary<Guid, int> Seasons { get; init; }
    }
}
