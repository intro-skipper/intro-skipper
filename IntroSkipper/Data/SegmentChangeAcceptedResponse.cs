// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>Wire response for an authoritative change whose projection did not apply synchronously.</summary>
/// <param name="ChangeId">Durable change identifier.</param>
/// <param name="ChangeStatus">Authoritative change status.</param>
/// <param name="Projections">Per-item projection status at response time.</param>
public sealed record SegmentChangeAcceptedResponse(Guid ChangeId, string ChangeStatus, IReadOnlyList<SegmentProjectionAcceptedResponse> Projections);
