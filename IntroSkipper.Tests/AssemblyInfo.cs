// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using Xunit;

// The test suite uses process-wide static state (e.g., `Plugin.Instance`).
// Run tests sequentially to avoid cross-test interference.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
