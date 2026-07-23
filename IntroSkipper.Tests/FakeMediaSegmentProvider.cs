// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;

/// <summary>
/// Minimal <see cref="IMediaSegmentProvider"/> fake carrying only a name, for
/// provider-name resolution in editor-view tests.
/// </summary>
internal sealed class FakeMediaSegmentProvider(string name) : IMediaSegmentProvider
{
    public string Name => name;

    public Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(MediaSegmentGenerationRequest request, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<MediaSegmentDto>>([]);

    public Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask<bool> Supports(BaseItem item) => ValueTask.FromResult(true);
}
