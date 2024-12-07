using System;
using System.Collections.Generic;

namespace IntroSkipper.Data
{
    /// <summary>
    /// /// Update analyzer actions request.
    /// </summary>
    public class UpdateSeasonRegexRequest
    {
        /// <summary>
        /// Gets or sets season ID.
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// Gets or sets analyzer actions.
        /// </summary>
        public IReadOnlyDictionary<AnalysisMode, string> SeasonRegexs { get; set; } = new Dictionary<AnalysisMode, string>();
    }
}
