# Recap detection — research findings & roadmap

This folder collects a structured research effort into **recap detection** for the Intro Skipper
plugin: what it should be, where the recently-merged implementation falls short, and an
evidence-based, phased plan to make it good. It is the index + executive summary for five RFCs and a
measured comparison produced as parallel spikes.

> **One-paragraph recommendation.** The recap feature shipped broken (see below) and, even now that it
> runs, its core design forces every recap to start at `0:00` — which makes it *skip the cold open*
> (real story) on a large class of shows. The highest-value, lowest-risk work is **(1) hardening the
> existing detector** to stop that harm, then **(2) adding a subtitle "Previously on" signal** behind a
> default-off flag to extend reach to recaps the audio path structurally cannot find. Treat the
> evaluation harness as a **regression guard, not validation** — real accuracy needs a labeled corpus
> of real episodes, which does not exist yet. Defer the cross-episode-reuse signal until a cache
> conflict is resolved. Do **not** add a visual-entropy tier.

---

## How we got here

Recap detection was added in **PR #771** ("Add optional recap detection fallback to early black
frame", AI-authored, merged 2026-06-19) and **shipped broken**: `QueuedEpisode.GetFingerprintRange`
had no `Recap` case, so recap fingerprinting threw `ArgumentException` (not caught as
`FingerprintException`) and aborted analysis. Commit `e17f044` ("fix recap", 2026-06-27) added the
missing `AnalysisMode.Recap => (0, IntroFingerprintEnd)` line. The feature therefore had **no real
end-to-end validation** before it merged, and every recap test in the repo is synthetic/unit-level.

That history motivated this research: rather than patch symptoms, map the whole signal landscape,
prototype the credible approaches, and *measure* them.

## What a recap is (and why it is hard)

A recap is the "Previously on…" montage near the start of an episode — **reused clips from earlier
episodes**, ~10–60 s, frequently bounded by black-frame/fade transitions. It surfaces to Jellyfin
clients as `MediaSegmentType.Recap`. The hard parts:

- **The content differs every episode** (different clips, different dialogue). The only repeatable
  audio is a short "Previously on" sting/jingle, which **many shows don't have**.
- **Placement varies**: before a cold open, after a cold open, or after the intro. Any heuristic that
  assumes "recap starts at 0" is wrong for two of those three shapes.
- **The false-positive surface is brutal**: the first 2–3 minutes of an episode are full of
  non-recap structure (idents, title cards, "3 days earlier" location cards, cold opens) that a
  naïve detector will mistake for a recap — and a *wrong* recap detection tells the client to **skip
  real story**, which is far worse than missing a recap.

## Core defects in the current implementation (all code-verified)

1. **Recap is structurally forced to start at `0:00`.** `ChapterAnalyzer.BuildRecapFromBlackFrames`
   hardcodes `(0, …)`; `ChromaprintAnalyzer.GetEarliestTimeRange` zeroes starts ≤ 5 s. On a
   cold-open-then-recap show this *skips the cold open*.
2. **The boundary "latest black frame before the intro" overshoots or fails** — it can swallow the
   episode opening, or return nothing when there is no fade.
3. **Recall is hostage to a shared audio sting** — needs ≥ 2 episodes sharing identical audio; fails
   on S01E01 and on shows with no sting.
4. **"After-intro" recaps are unreachable** — the search window is capped at the detected intro
   start.
5. **False-positive risk from the opening theme** — "earliest shared region" can select the title
   theme when the intro wasn't detected.
6. **A dead UI lever** — `AnalyzerAction.BlackFrame` is a no-op for recap (no black-frame analyzer is
   ever added to the recap chain), and the black-frame fallback sits at *chapter* priority where it
   can pre-empt the better sting signal.
7. **A redundant decode** — recap re-fingerprints the identical opening audio the Introduction pass
   already decoded (separate cache key per mode).

## The signal landscape (ranked)

| Tier | Signal | Precision | Coverage | Cost | Verdict |
| --- | --- | --- | --- | --- | --- |
| 1 | **Chapters** (`Recap`/`Previously` marker, SponsorBlock label) | Highest | Low (rarely present) | Lowest | Keep first; already works. |
| 2 | **Subtitles** — anchored "Previously on" phrase + cue cluster ([RFC A](./A-subtitles.md)) | High *when text subs transcribe the recap* | Content-dependent | **Lowest** (no A/V decode) | **Add, behind a default-off flag.** The biggest new reach; finds non-zero-start and after-intro recaps with no cross-episode comparison. |
| 3 | **Hardened sting + black frame** ([RFC C](./C-harden.md)) | Medium→High | Unchanged/narrower | ~free (shared fingerprint) | **Do first.** Fixes the *harm* in the shipped detector. |
| 4 | **Cross-episode reuse** ([RFC B](./B-cross-episode.md)) | High for *original-audio, non-remixed* reuse | Narrow (re-mix/no-reuse defeats it) | Highest (full-episode decode) | **Defer** — real model of a recap, but breaks a cache assumption (below) and is unvalidated on real data. |
| — | **Visual entropy/saturation** (à la PR #798) | Low (card ≠ recap card without OCR) | Low (burned-in card only) | Low | **Not a tier.** Recap content is high-entropy moving footage; the "Previously on" card is on-screen ~1–3 s and fails #798's sustained-run test. At most a last-resort corroborator *with OCR*. |

The orchestration backbone (schema, metrics, runner) and the analysis of the chain *as an ensemble*
are in [RFC D](./D-ensemble-eval.md); the independent adversarial review is in
[the red-team](./R2-red-team.md).

## The measured comparison (directional, not validation)

From [the integration spike](./R2-integration-measurement.md): the real spike-A and spike-C code run
over a 36-scenario synthetic-representative dataset (23 with a recap, 13 without), scored with
**harm-aware** metrics (content-skip seconds; false negatives split into *silent miss* vs
*fired-but-wrong*).

| metric | baseline (shipped) | +C hardening | +A subtitles | **+A+C ensemble** |
| --- | --- | --- | --- | --- |
| recall | 0.435 | 0.696 | 0.870 | **0.913** |
| false-positive rate | 0.308 | 0.077 | 0.308 | **0.077** |
| fired-but-wrong (harmful) | **6** | 0 | 1 | **0** |
| content-skip seconds (harm) | **325** | 0 | 55 | **0** |
| F1 | 0.541 | 0.800 | 0.851 | **0.933** |

Reading: **C removes the harm** (content-skip `325 s → 0`, fired-but-wrong `6 → 0`, FP
`0.308 → 0.077`); **A adds the reach** (after-intro recall `0.20 → 0.80`, recovers no-sting recaps);
they are **complementary**, and a shared boundary-reconciliation step gives A's localizations
C-quality starts. The shipped baseline's real failure isn't low recall — it's that on cold-open
recaps it **fires on the wrong span and skips ~46 s of story per episode**.

**This is directional only.** The harness scores interval geometry; it does **not** decode audio,
frames, or subtitles — the per-episode signals are *modeled* (authored cleanly), and clean synthetic
inputs flatter every detector (note the all-zero end-error). It proves the *logic composes and the
relative behavior*; it does **not** prove real-world accuracy or pick a production default.

## Roadmap

**Phase 0 — Build the real-media validation harness (prerequisite for any "default on").**
Adopt RFC D's harness as a regression guard; extend its schema with per-signal availability labels
and its metrics with the harm-aware measures already prototyped. Assemble a labeled corpus of **real**
episodes (order 30–50 per shape per major genre; a no-recap majority; ≥ 3 languages incl. non-Latin;
multiple contributors), scored via the `RecapDetection.FromInterval` adapter on ≥ 2 ffmpeg builds.
Until this exists, every accuracy claim is a hypothesis.

**Phase 1 — Hardening (RFC C), highest priority.** Stop forcing the start to `0:00`; anchor it to the
cold-open fade. Replace "latest black frame" with the earliest *duration-valid* montage end. Add the
opening-theme false-positive guard. Reuse the Introduction fingerprint to remove the redundant decode.
Net effect (measured, directional): the cold-open story-skip and false positives drop sharply with no
new dependency. **This fixes the currently-shipping, just-unbroken detector.**

**Phase 2 — Subtitles (RFC A), behind a default-off flag.** A new subtitle tier (probe → windowed
text extract → anchored multilingual phrase match) that produces a correct non-zero start and reaches
after-intro/no-sting recaps. Default off until Phase 0 produces numbers; harden the non-Latin phrase
lists (bare high-frequency words are a false-positive risk) before it defaults on.

**Phase 3 — One shared boundary step + tier orchestration.** Land the single tier-agnostic
`ReconcileBoundaries` (start resolution + black-frame end snap) so subtitle and sting tiers agree on
boundaries, and wire the named precedence chain (Chapter → Subtitle → hardened sting) into
`BaseItemAnalyzerTask`. Demote the black-frame fallback out of chapter-tier priority. Also fix the
residual start corruptor: `AdjustIntroBasedOnChapters` can still pull a recap start.

**Phase 4 — Cross-episode reuse (RFC B), deferred research.** The most faithful model of a recap and
cheap enough on the audio path, but it must not enter the fingerprint cache until the **range-ownership
conflict with C is resolved** (B wants Introduction to fingerprint the *full* episode; C reuses the
*opening* Introduction fingerprint by exact `Start/End` cache key — adopting B as written silently
breaks C's dedup and perturbs intro detection) and its quality is shown on *real* data, not
uniform-random fingerprints.

**Not on the roadmap:** a visual-entropy recap tier (see the table). Revisit only if an OCR signal is
added, in which case it becomes a burned-in-card corroborator for the subtitle tier.

## Open issues carried forward (from the red-team)

- C's "earliest shared region" selection is unchanged and can still bias the start early, partially
  undermining C's own cold-open fix.
- A's `ja`/`ko` default phrases are bare high-frequency words → false-positive risk; needs anchoring
  review.
- The harness cannot distinguish a "skipped real story" fire from a silent miss **unless** the
  harm-aware metrics from the integration spike are merged into RFC D's core.
- B vs C fingerprint-cache range ownership (Phase 4 gate).

## Index

| Doc | Subject |
| --- | --- |
| [A-subtitles.md](./A-subtitles.md) | Subtitle "Previously on" phrase detection (PR #805) |
| [B-cross-episode.md](./B-cross-episode.md) | Cross-episode content-reuse matching (PR #807) |
| [C-harden.md](./C-harden.md) | Hardening the shipped sting + black-frame path (PR #806) |
| [D-ensemble-eval.md](./D-ensemble-eval.md) | Ensemble orchestration + evaluation harness (PR #808) |
| [R2-integration-measurement.md](./R2-integration-measurement.md) | Integrated A+C measurement, harm-aware metrics (PR #808) |
| [R2-red-team.md](./R2-red-team.md) | Adversarial review of all of the above (PR #809) |
