// SPDX-FileCopyrightText: 2025-2026 rlauuzo
// SPDX-FileCopyrightText: 2025-2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

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
        private const string TimeoutAssignmentPattern = @"(?<keep>[A-Za-z_$][\w$]*\.keep\|\|)?\((?<button>[A-Za-z_$][\w$]*)\.hideTimeout=setTimeout\(\k<button>\.hideSkipButton\.bind\(\k<button>\),8e3\)\)";

        /// <summary>
        /// Pattern to match the timeout check in the hideSkipButton method.
        /// </summary>
        private const string TimeoutOsdChangePattern = @"\:this\.hideTimeout\|\|this\.hideSkipButton\(\)";

        /// <summary>
        /// Pattern to match the focusability check in the showSkipButton method.
        /// </summary>
        private const string FocusabilityAssignmentPattern =
            @"(?:(?:var)\s+)?[A-Za-z_$][\w$]*\s*=\s*document\.activeElement\s*&&\s*[A-Za-z_$][\w$]*\.A\.isCurrentlyFocusable\(document\.activeElement\)";

        /// <summary>
        /// Pattern to match the default Intro segment action (AskToSkip) in the action map.
        /// </summary>
        private const string IntroActionDefaultPattern =
            @"\[(?<mod>[A-Za-z_$][\w$]*)\.w\.Intro\]=(?<act>[A-Za-z_$][\w$]*)\.M\.AskToSkip";

        /// <summary>
        /// Pattern to match the default Outro/Credits segment action (AskToSkip) in the action map.
        /// </summary>
        private const string OutroActionDefaultPattern =
            @"\[(?<mod>[A-Za-z_$][\w$]*)\.w\.Outro\]=(?<act>[A-Za-z_$][\w$]*)\.M\.AskToSkip";

        /// <summary>
        /// Pattern to match the segment bounds check in onPlayerTimeUpdate that controls skip button visibility.
        /// </summary>
        private const string SegmentBoundsCheckPattern =
            @"(?<check>[A-Za-z_$][\w$]*)\(this\.currentSegment,(?<pos>[A-Za-z_$][\w$]*)\)\|\|\(this\.currentSegment=null,this\.hideSkipButton\(\)\)";

        /// <summary>
        /// Pattern to match the showSkipButton function opening to inject an early-return guard.
        /// </summary>
        private const string ShowSkipButtonPattern =
            @"showSkipButton=function\((?<arg>[A-Za-z_$][\w$]*)\)\{";

        /// <summary>
        /// Number of milliseconds per second.
        /// </summary>
        private const int MillisecondsPerSecond = 1000;

        /// <summary>
        /// Ticks per second (Jellyfin uses 100-nanosecond tick intervals).
        /// </summary>
        private const long TicksPerSecond = 10_000_000L;

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

        [GeneratedRegex(IntroActionDefaultPattern)]
        private static partial Regex IntroActionDefaultRegex();

        [GeneratedRegex(OutroActionDefaultPattern)]
        private static partial Regex OutroActionDefaultRegex();

        [GeneratedRegex(SegmentBoundsCheckPattern)]
        private static partial Regex SegmentBoundsCheckRegex();

        [GeneratedRegex(ShowSkipButtonPattern)]
        private static partial Regex ShowSkipButtonRegex();

        /// <summary>
        /// Transforms the file contents by modifying skip button behavior, segment actions, and visibility timing.
        /// </summary>
        /// <param name="payload">The payload containing the file contents to transform.</param>
        /// <returns>The transformed file contents.</returns>
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
            var persist = !TryGetValidTimeoutMs(config.SkipbuttonHideDelay, out var hideDelayMs);

            // Replace the hardcoded 8e3 (8000 ms) timeout with our configurable value
            var updated = ReplaceTimeoutAssignment(contents, persist, hideDelayMs.ToString(CultureInfo.InvariantCulture));

            // Replace the timeout check in hideSkipButton to respect the persist setting
            updated = ReplaceTimeoutOsdChange(updated, persist);

            // Force skip button focus during early playback for TV remotes
            updated = ReplaceFocusabilityCheck(updated);

            // Override default segment actions from AskToSkip to Skip when configured
            if (config.AutoSkipIntro)
            {
                updated = ReplaceIntroActionDefault(updated);
            }

            if (config.AutoSkipCredits)
            {
                updated = ReplaceOutroActionDefault(updated);
            }

            // Hide skip button N seconds before segment end and block all re-show paths
            if (config.SkipButtonVisibleSeconds > 0)
            {
                var thresholdTicks = ((long)config.SkipButtonVisibleSeconds * TicksPerSecond).ToString(CultureInfo.InvariantCulture);
                var floorTicks = ((long)hideDelayMs * TicksPerSecond / MillisecondsPerSecond).ToString(CultureInfo.InvariantCulture);
                updated = ReplaceSegmentBoundsCheck(updated, thresholdTicks, floorTicks);
                updated = InjectShowSkipButtonGuard(updated, thresholdTicks, floorTicks);
            }

            return updated;
        }

        /// <summary>
        /// Replaces the hardcoded 8-second auto-hide timeout with a configurable delay,
        /// or removes it entirely to make the skip button persistent.
        /// </summary>
        /// <param name="contents">The JavaScript content to modify.</param>
        /// <param name="persist">true if the skip button should be persistent; otherwise, false.</param>
        /// <param name="hideDelayMs">The delay in milliseconds before hiding the skip button.</param>
        /// <returns>The modified content.</returns>
        private static string ReplaceTimeoutAssignment(string contents, bool persist, string hideDelayMs) => TimeoutAssignmentRegex().Replace(contents, m => persist ? "true" : $"{m.Groups["keep"].Value}({m.Groups["button"].Value}.hideTimeout=setTimeout({m.Groups["button"].Value}.hideSkipButton.bind({m.Groups["button"].Value}),{hideDelayMs}))");

        /// <summary>
        /// Suppresses the immediate-hide on OSD close when the skip button is persistent.
        /// </summary>
        /// <param name="contents">The JavaScript content to modify.</param>
        /// <param name="persist">true if the skip button should be persistent; otherwise, false.</param>
        /// <returns>The modified content.</returns>
        private static string ReplaceTimeoutOsdChange(string contents, bool persist) => persist ? TimeoutOsdChangeRegex().Replace(contents, ":true") : contents;

        /// <summary>
        /// Forces focus onto the skip button during the first second of playback for TV remote UX.
        /// </summary>
        /// <param name="contents">The JavaScript content to modify.</param>
        /// <returns>The modified content.</returns>
        private static string ReplaceFocusabilityCheck(string contents) => FocusabilityAssignmentRegex().Replace(contents, m => m.Value + $"&&t.playbackManager.currentTime()>{MillisecondsPerSecond}");

        /// <summary>
        /// Changes the default Intro segment action from AskToSkip to Skip (auto-skip).
        /// </summary>
        /// <param name="contents">The JavaScript content to modify.</param>
        /// <returns>The modified content.</returns>
        private static string ReplaceIntroActionDefault(string contents) =>
            IntroActionDefaultRegex().Replace(contents, m => $"[{m.Groups["mod"].Value}.w.Intro]={m.Groups["act"].Value}.M.Skip");

        /// <summary>
        /// Changes the default Outro/Credits segment action from AskToSkip to Skip (auto-skip).
        /// </summary>
        /// <param name="contents">The JavaScript content to modify.</param>
        /// <returns>The modified content.</returns>
        private static string ReplaceOutroActionDefault(string contents) =>
            OutroActionDefaultRegex().Replace(contents, m => $"[{m.Groups["mod"].Value}.w.Outro]={m.Groups["act"].Value}.M.Skip");

        /// <summary>
        /// Modifies onPlayerTimeUpdate to actively hide the skip button N seconds before segment end.
        /// Uses Math.max(StartTicks+floor, EndTicks-threshold) so the button always shows for at least the hide delay duration.
        /// Keeps currentSegment set to prevent onPromptSkip from re-triggering.
        /// </summary>
        private static string ReplaceSegmentBoundsCheck(string contents, string thresholdTicks, string floorTicks) =>
            SegmentBoundsCheckRegex().Replace(contents, m =>
            {
                var chk = m.Groups["check"].Value;
                var pos = m.Groups["pos"].Value;
                var cutoff = $"Math.max(this.currentSegment.StartTicks+{floorTicks},this.currentSegment.EndTicks-{thresholdTicks})";
                return $"{chk}(this.currentSegment,{pos})" +
                    $"?({pos}>={cutoff}&&this.hideSkipButton())" +
                    $":(this.currentSegment=null,this.hideSkipButton())";
            });

        /// <summary>
        /// Injects an early-return guard into showSkipButton so the button cannot be re-shown
        /// (by OSD changes or any other caller) once the visibility threshold has been reached.
        /// </summary>
        private static string InjectShowSkipButtonGuard(string contents, string thresholdTicks, string floorTicks) =>
            ShowSkipButtonRegex().Replace(contents, m =>
            {
                var arg = m.Groups["arg"].Value;
                var pos = "this.playbackManager.currentTime(this.player)*1e4";
                var cutoff = $"Math.max(this.currentSegment.StartTicks+{floorTicks},this.currentSegment.EndTicks-{thresholdTicks})";
                return $"showSkipButton=function({arg}){{" +
                    $"if(this.currentSegment&&{pos}>={cutoff})return;";
            });

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
