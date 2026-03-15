// SPDX-FileCopyrightText: 2025-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2025-2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace IntroSkipper.Helper
{
    /// <summary>
    /// A class responsible for injecting a script into the jellyfin web.
    /// </summary>
    public static partial class Injector
    {
        /// <summary>
        /// Pattern to match the timeout assignment in the showSkipButton method.
        /// </summary>
        private const string TimeoutAssignmentPattern = @"\(t\.hideTimeout=setTimeout\(t\.hideSkipButton\.bind\(t\)\,8e3\)\)";

        /// <summary>
        /// Pattern to match the timeout check in the hideSkipButton method.
        /// </summary>
        private const string TimeoutOsdChangePattern = @"\:this\.hideTimeout\|\|this\.hideSkipButton\(\)";

        /// <summary>
        /// Regex to match the focusability check.
        /// </summary>
        private const string FocusabilityAssignmentPattern =
            @"(?:(?:var)\s+)?r\s*=\s*document\.activeElement\s*&&\s*[A-Za-z_$][\w$]*\.A\.isCurrentlyFocusable\(document\.activeElement\)";

        /// <summary>
        /// Number of milliseconds per second.
        /// </summary>
        private const int MillisecondsPerSecond = 1000;

        /// <summary>
        /// Maximum safe number of seconds that can be converted to milliseconds without overflow.
        /// </summary>
        private const int MaxSafeSeconds = int.MaxValue / MillisecondsPerSecond;

        [GeneratedRegex(TimeoutAssignmentPattern)]
        private static partial Regex TimeoutAssignmentRegex();

        [GeneratedRegex(TimeoutOsdChangePattern)]
        private static partial Regex TimeoutOsdChangeRegex();

        [GeneratedRegex(FocusabilityAssignmentPattern, RegexOptions.CultureInvariant)]
        private static partial Regex FocusabilityAssignmentRegex();

        /// <summary>
        /// Transforms the file contents by modifying JavaScript timeout values.
        /// </summary>
        /// <param name="payload">The payload containing the file contents to transform.</param>
        /// <returns>The transformed file contents with modified timeout values.</returns>
        /// <exception cref="ArgumentNullException">Thrown when payload is null.</exception>
        public static string FileTransformer(PayloadRequest payload)
        {
            ArgumentNullException.ThrowIfNull(payload);

            var contents = payload.Contents ?? string.Empty;
            if (string.IsNullOrEmpty(contents))
            {
                return contents;
            }

            var config = Plugin.Instance?.Configuration;
            if (config is null || !config.UseFileTransformationPlugin)
            {
                return contents;
            }

            // Validate and get the timeout value
            bool persist = !TryGetValidTimeoutMs(config.SkipbuttonHideDelay, out var hideDelayMs);
            string persistStr = persist ? "true" : "false";

            // Replace the hardcoded 8e3 (8000 ms) timeout with our configurable value
            var updated = ReplaceTimeoutAssignment(contents, persistStr, hideDelayMs.ToString(CultureInfo.InvariantCulture));

            // Replace the timeout check in hideSkipButton to respect the persist setting
            updated = ReplaceTimeoutOsdChange(updated, persistStr);

            // Add playback time condition to focusability check to gate skip button behavior
            updated = ReplaceFocusabilityCheck(updated);

            return updated;
        }

        /// <summary>
        /// Makes the skip button persistent by removing the setTimeout call that auto-hides it.
        /// </summary>
        /// <param name="contents">The JavaScript content to modify.</param>
        /// <param name="persist">true if the skip button should be persistent; otherwise, false.</param>
        /// <param name="hideDelayMs">The delay in milliseconds before hiding the skip button.</param>
        /// <returns>The modified content with persistent skip button behavior.</returns>
        private static string ReplaceTimeoutAssignment(string contents, string persist, string hideDelayMs) => TimeoutAssignmentRegex().Replace(contents, $"{persist}||(t.hideTimeout=setTimeout(t.hideSkipButton.bind(t),{hideDelayMs}))");

        /// <summary>
        /// Makes the skip button persistent by removing the setTimeout call that auto-hides it.
        /// </summary>
        /// <param name="contents">The JavaScript content to modify.</param>
        /// <param name="persist">true if the skip button should be persistent; otherwise, false.</param>
        /// <returns>The modified content.</returns>
        private static string ReplaceTimeoutOsdChange(string contents, string persist) => TimeoutOsdChangeRegex().Replace(contents, $":{persist}||this.hideTimeout||this.hideSkipButton()");

        /// <summary>
        /// Adds a current time check to the focusability condition.
        /// </summary>
        /// <param name="contents">The JavaScript content to modify.</param>
        /// <returns>The modified content.</returns>
        private static string ReplaceFocusabilityCheck(string contents) => FocusabilityAssignmentRegex().Replace(contents, m => m.Value + $"&&t.playbackManager.currentTime()>{MillisecondsPerSecond}");

        /// <summary>
        /// Attempts to convert seconds to milliseconds with validation and overflow protection.
        /// </summary>
        /// <param name="seconds">The number of seconds to convert.</param>
        /// <param name="milliseconds">When this method returns, contains the equivalent milliseconds if the conversion succeeded, or 0 if it failed.</param>
        /// <returns>true if the conversion succeeded; otherwise, false.</returns>
        private static bool TryGetValidTimeoutMs(int seconds, out int milliseconds)
        {
            var valid = seconds > 0 && seconds <= MaxSafeSeconds;
            milliseconds = valid ? seconds * MillisecondsPerSecond : 0;
            return valid;
        }
    }
}
