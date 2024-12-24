using System;
using System.Collections.Generic;

namespace IntroSkipper.Data
{
    /// <summary>
    /// /// Update analyzer actions request.
    /// </summary>
    public class UpdateSeasonConfigRequest
    {
        /// <summary>
        /// Gets or sets analyzer actions.
        /// </summary>
        public IReadOnlyDictionary<AnalysisMode, AnalyzerAction> AnalyzerActions { get; set; } = new Dictionary<AnalysisMode, AnalyzerAction>();

        /// <summary>
        /// Gets or sets analyzer actions.
        /// </summary>
        public IReadOnlyDictionary<AnalysisMode, string> SeasonRegexs { get; set; } = new Dictionary<AnalysisMode, string>();
    }
}
