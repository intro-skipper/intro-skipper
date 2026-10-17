// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Remote;

/// <summary>
/// Selects one pending server job for explicit offline manifest export.
/// </summary>
/// <param name="JobId">The pending durable job to export.</param>
public sealed record RemoteManifestExportRequest(Guid JobId);
