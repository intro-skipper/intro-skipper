# Upstream 12.0.6.0 integration validation

Target: stable `12.0/v12.0.6.0`, `da3d8d50af3418ecd1ba80c58eefa48df5b5e2b4`. The remote-node feature branch retains its protocol-3 bridge, queue controls, durable recovery, portable analysis environment and hardware decoder support. The compatibility revision compiles against Jellyfin 12.0.0, the supported minimum, instead of the upstream dependency target 12.2.0.

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

## Jellyfin 12.0 compatibility correction

The stable 12.0 server packages use EF Core 10.0.11 and UTF.Unknown 2.7.0, matching the existing plugin pins. Both Jellyfin package references now use 12.0.0; the complete upstream detector and remote-node code compiles without API changes. The same plugin DLL passed isolated load-context checks against Jellyfin Controller/Model 12.0.0, 12.1.0 and 12.2.0: all 858 plugin types resolved and the server service-registration interface was instantiated in each host. These checks load only the two installable DLLs and use Jellyfin's dependency-resolver pattern. They are not a running-server acceptance test.

The node now accepts Jellyfin 12.0 or newer when the matching protocol-3 capabilities are present, rather than enumerating only 12.1 and 12.2. Handshake coverage includes 12.0, 12.0.1, 12.1, 12.2, 12.3 and 13.0, rejecting older/malformed versions and invalid authentication. Future-version cases check the HTTP capability gate, not future server binary compatibility.

After the compatibility correction, the full plugin suite passed 1,374/1,374 and the node suite passed 57/57, with no skipped tests.
