// SPDX-FileCopyrightText: 2025-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using Xunit;

// The test suite uses process-wide static state (e.g., `Plugin.Instance`).
// Run tests sequentially to avoid cross-test interference.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
