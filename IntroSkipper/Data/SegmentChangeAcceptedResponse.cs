// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Wire body of a <c>202 Accepted</c> mutation response: the authoritative change
/// committed durably, but its Jellyfin projection did not apply synchronously — the
/// journaled work is retried until Jellyfin converges (or, while mirroring is
/// disabled, replays on re-enable).
/// </summary>
/// <param name="ChangeStatus">Authoritative change status; always <c>"Accepted"</c>.</param>
/// <param name="Projection">Projection status at response time: <c>"Pending"</c> or <c>"Skipped"</c> (mirroring disabled).</param>
/// <param name="Segments">The committed segment values the change affected.</param>
public sealed record SegmentChangeAcceptedResponse(string ChangeStatus, string Projection, IReadOnlyList<SegmentDto> Segments);
