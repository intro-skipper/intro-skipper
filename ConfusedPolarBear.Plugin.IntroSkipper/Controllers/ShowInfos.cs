using System;
using System.Collections.Generic;

namespace ConfusedPolarBear.Plugin.IntroSkipper.Controllers
{
    /// <summary>
    /// Enthält Informationen über eine Show.
    /// </summary>
    public class ShowInfos
    {
        /// <summary>
        /// Gets or sets the ID of the show.
        /// </summary>
        public required string SeriesName { get; set; }

        /// <summary>
        /// Gets or sets the Library of the show.
        /// </summary>
        public required string LibraryName { get; set; }

        /// <summary>
        /// Gets or sets the Seasons of the show.
        /// </summary>
#pragma warning disable CA2227 // Sammlungseigenschaften müssen schreibgeschützt sein
        public required Dictionary<Guid, string> Seasons { get; set; }
#pragma warning restore CA2227 // Sammlungseigenschaften müssen schreibgeschützt sein
    }
}
