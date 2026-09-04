// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Helper;

namespace IntroSkipper.Manager;

/// <summary>
/// The per-item mutation stripes that serialize every interactive segment mutation:
/// the durable segment-change coordinator takes an item's stripe both to apply an
/// intent and to project its journaled work, so a projection can never interleave
/// with a concurrent mutation on the same item. Separate pool from
/// <see cref="MediaSegmentMirror"/>'s; lock order is always mutation stripe -&gt;
/// mirror stripe. A distinct type so DI can hold exactly one pool per role; must be
/// registered as a singleton so all requests share the stripes.
/// </summary>
internal sealed class SegmentMutationLocks : StripedAsyncLock;
