// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Db;
using IntroSkipper.Helper;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;

namespace IntroSkipper.Providers
{
    /// <summary>
    /// Introskipper media segment provider.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="SegmentProvider"/> class.
    /// </remarks>
    /// <param name="database">Segment database facade.</param>
    /// <param name="segmentDtoFactory">Converts plugin segments to Jellyfin DTOs.</param>
    public class SegmentProvider(IntroSkipperDatabase database, SegmentDtoFactory segmentDtoFactory) : IMediaSegmentProvider
    {
        private readonly IntroSkipperDatabase _database = database;
        private readonly SegmentDtoFactory _segmentDtoFactory = segmentDtoFactory;

        /// <inheritdoc/>
        public string Name => Plugin.Instance!.Name;

        /// <inheritdoc/>
        public async Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(MediaSegmentGenerationRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            return await _segmentDtoFactory.CreateAsync(request.ItemId, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Erases the item's automatic segments and analysis records after its media file
        /// changed. Jellyfin calls this from its metadata refresh, then deletes the item's
        /// rows from its MediaSegments table.
        /// </summary>
        /// <remarks>
        /// The item still exists, so its user segments and tombstones stay, as in the
        /// plugin's own replaced-file reset. The erase journals the item, so the projection
        /// worker pushes the kept user segments back after Jellyfin's delete. A worker poll
        /// that lands between this call and that delete pushes too early, and Jellyfin then
        /// lacks the user segments until the item's next projection or Jellyfin's next
        /// media segment scan.
        /// </remarks>
        /// <param name="itemId">The item whose media file changed.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that completes when the erase is committed.</returns>
        public async Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken)
        {
            await _database.EraseItemsAsync([itemId], eraseTombstones: false, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public ValueTask<bool> Supports(BaseItem item) => ValueTask.FromResult(MediaItemHelper.IsSupported(item));
    }
}
