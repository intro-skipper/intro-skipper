using System;
using System.Collections.Generic;

namespace ConfusedPolarBear.Plugin.IntroSkipper.Controllers
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
        /// Gets or sets the Library of the show.
        /// </summary>
        public required string LibraryName { get; set; }

        /// <summary>
        /// Gets the Seasons of the show.
        /// </summary>
        public required Dictionary<Guid, string> Seasons { get; init; }
    }
}
