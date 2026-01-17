using System.Collections.Generic;

namespace IntroSkipper.Data;

/// <summary>
/// Represents keyframe data extracted from a video file.
/// All timestamps are normalized to seconds for consistency across extraction methods.
/// </summary>
/// <param name="Duration">Video duration in seconds (normalized from source format).</param>
/// <param name="Keyframes">List of keyframe timestamps in seconds (normalized from source format).</param>
public record KeyframeData(double Duration, IReadOnlyList<double> Keyframes);
