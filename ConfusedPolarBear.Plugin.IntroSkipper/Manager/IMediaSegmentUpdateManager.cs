using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ConfusedPolarBear.Plugin.IntroSkipper.Data;

namespace ConfusedPolarBear.Plugin.IntroSkipper.Manager;

/// <summary>
/// Media file analyzer interface.
/// </summary>
public interface IMediaSegmentUpdateManager
{
    /// <summary>
    /// Analyze media files for shared introductions or credits, returning all media files that were **not successfully analyzed**.
    /// </summary>
    /// <param name="episodes">Collection of unanalyzed media files.</param>
    /// <param name="cancellationToken">CancellationToken.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task UpdateMediaSegments(IReadOnlyList<QueuedEpisode> episodes, CancellationToken cancellationToken);
}
