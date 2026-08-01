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
    public class SegmentProvider(IIntroSkipperDatabase database, SegmentDtoFactory segmentDtoFactory) : IMediaSegmentProvider
    {
        private readonly IIntroSkipperDatabase _database = database;
        private readonly SegmentDtoFactory _segmentDtoFactory = segmentDtoFactory;

        /// <inheritdoc/>
        public string Name => Plugin.Instance!.Name;

        /// <inheritdoc/>
        public async Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(MediaSegmentGenerationRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            return await _segmentDtoFactory.CreateAsync(request.ItemId, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken)
        {
            await _database.DeleteItemSegmentsAsync(itemId, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public ValueTask<bool> Supports(BaseItem item) => ValueTask.FromResult(MediaItemHelper.IsSupported(item));
    }
}
