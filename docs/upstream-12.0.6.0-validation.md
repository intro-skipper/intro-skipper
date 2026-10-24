# Upstream 12.0.6.0 integration validation

Target: stable `12.0/v12.0.6.0`, `da3d8d50af3418ecd1ba80c58eefa48df5b5e2b4`. The remote-node feature branch retains its protocol-3 bridge, queue controls, durable recovery, portable analysis environment and hardware decoder support. Jellyfin 12.2.0 remains the minimum server version.

The merge combines upstream chapter authority, retained credits on failed scans, settled-season local Chromaprint consensus, manual Season 0 handling and cut-window probing with scoped node ownership. Optional job flags carry the consensus and manual-scan choices to the node. Original-credits CPU/GPU profile identities advance to v2; older job snapshots default the added flags off. No new database migration is introduced by this integration.

Validated on Windows with .NET SDK 10.0.303:

- Plugin solution build, including the TypeScript/Vite dashboard: zero warnings and errors.
- Plugin tests: 1,374 passed, zero skipped; media tests use Jellyfin FFmpeg 8.1.3.
- Node solution build: zero warnings and errors; 51 tests passed, zero skipped.
- Dashboard tests: four passed.
- Before/after node scans with Jellyfin FFmpeg 7.1.3 on twelve available season-five episodes: intro boundaries were identical. Combined credits produced segments for eleven episodes in both versions. One start moved approximately 0.248 seconds later, and its end moved less than 0.001 seconds. Results remained held for review; no server imports or sidecars were written.
- CreditsRunner keyframe baseline/candidate runs on fourteen files had no failures. The twelve episodes with existing human labels had zero new regressions. Neither version found those twelve credits with the keyframe analyzer alone, so this is regression evidence, not an accuracy claim. The labels are calibration data, not an independent holdout.
- CreditsRunner with Jellyfin FFmpeg 8.1.3 completed the twelve-episode Introduction chain and whole credits pass with no failed episodes: twelve intro results and eleven credits results.

The baseline node source was the original combined development workspace; the candidate is the merged published fork and node. The media checks do not establish live Jellyfin acceptance or GPU decoder coverage. Install both updated components together, reconnect the node to advertise its new detector profiles, and queue fresh work.
