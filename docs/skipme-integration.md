# SkipMe analyzer integration

Integration is **off by default**. Installing compatible versions of both plugins
does not change SkipMe's standalone provider. To opt in, enable **Use Intro Skipper
for segment analysis** on SkipMe's Sync tab, save settings, and restart Jellyfin.

With that preference enabled and a compatible host installed, SkipMe registers its
local segment reader with Intro Skipper instead of publishing segments through a
separate Jellyfin media segment provider. SkipMe still owns its database sync,
configuration, and sharing features. Intro Skipper owns analysis and publication
to Jellyfin. Changing the preference requires another restart; saving settings
does not switch the running provider.

## Precedence

For each item and segment type, a valid SkipMe match is authoritative. Its timestamps
are stored unchanged with `SegmentSource.SkipMe`; chapter matching, fingerprinting,
black-frame detection, boundary adjustments, and the credits combiner do not run for
that item and type. If SkipMe has no valid match, the existing local analysis runs.
Authoritative items are also excluded from neighboring episodes' fingerprint
comparisons, so fallback detection may have fewer episodes available to compare.

Manual segments and deletion tombstones retain their existing protection. A
suppressed SkipMe match does not trigger local fallback. An authoritative preview
replaces older credits-derived previews and prevents credits analysis from adding a
derived preview beside it. Disabled analysis modes and the per-season `None` action
remain disabled. Intro Skipper's library selection and exclusions still apply.

The source snapshot rejects segments for another item, unknown segment types,
negative or reversed ranges, and ranges extending beyond the file's runtime. Multiple
valid ranges of a type are retained; exact duplicates are removed.

The local credits-versus-intro overlap heuristic does not reject authoritative
SkipMe credits. Existing intro rows are left unchanged; manual credits and credits
tombstones still block overlapping incoming credits ranges. A fully rejected write
settles the mode according to any active rows left in storage, without running local
fallback.

## Freshness and failures

Queue verification reads SkipMe once per item. The validated snapshot is shared by
classification and analysis, so a concurrent sync cannot stamp an old match with a new
input hash. Canonically ordered ticks are included in the analysis hash only for the
types with matches. New, changed, removed, or disabled matches reopen the affected
analysis on the next run, including previously cached "no segments" results.
Unchanged data does not invalidate analysis simply because a sync ran.

Removing SkipMe returns affected items to their ordinary analysis hashes and local
fallback on the next analysis run. Source lookup failures skip the item for that run
rather than treating an unavailable source as an empty database and replacing its
segments. Cancellation propagates through the source read.

## Optional registration boundary

The public entry point is:

```csharp
IntroSkipper.Integrations.SkipMeIntegration.RegisterV1(
    IServiceCollection services,
    Func<IServiceProvider, IMediaSegmentProvider> providerFactory)
```

This method lives in `IntroSkipper.dll`. Its signature uses only framework and
Jellyfin types, allowing SkipMe to discover the versioned entry point without a hard
assembly dependency or a bundled copy of Intro Skipper. The factory is evaluated
when the shared DI container resolves the integration, after service registration
has finished. The provider is an input reader, not a Jellyfin provider registration.

SkipMe reads its saved `EnableIntroSkipperIntegration` preference before registration
using Jellyfin's existing configuration-path and XML-serializer instances. Missing,
legacy, disabled, or unreadable configuration does not opt in. No secondary service
container is built to load the preference.

SkipMe must keep its standalone provider when consent is absent or the compatible
entry point is unavailable.
When registration succeeds, SkipMe must stop registering that provider with Jellyfin,
route successful syncs to `IntroSkipperDetectSegmentsTask`, and retire only its own
previously published Jellyfin segments during handover. Intro Skipper's normal
database journal and mirror publish the resulting authoritative segments.
