#!/usr/bin/env bash
# Ablation driver (env-gated): add temporary runtime gates to each analyzer layer,
# build ONCE, then run the REAL analyzer over the corpus toggling layers via env
# vars (no per-variant rebuild, no dead-code warnings), score each, then revert.
set -euo pipefail

REPO=/home/intro-skipper
CR=$REPO/tools/credit-research
ABL=$CR/theory3_refine/ablation
ANALYZER=$REPO/IntroSkipper/Analyzers/CreditsBlackFrameAnalyzer.cs
POLICY=$REPO/IntroSkipper/Analyzers/Credits/CreditDetectionPolicy.cs
mkdir -p "$ABL"
cd "$CR"

git -C "$REPO" checkout -- "$ANALYZER" "$POLICY"

# --- inject temporary env gates -------------------------------------------------
python3 - <<'PY'
A="/home/intro-skipper/IntroSkipper/Analyzers/CreditsBlackFrameAnalyzer.cs"
s=open(A).read()
s=s.replace(
 "var boundaryRefiner = _config.RefineCreditsBoundary ? new CreditsBoundaryRefiner(_ffmpegService) : null;",
 'var boundaryRefiner = (_config.RefineCreditsBoundary && Environment.GetEnvironmentVariable("ABLATE_BOUNDARY") != "1") ? new CreditsBoundaryRefiner(_ffmpegService) : null;')
s=s.replace(
 "            blackIntervals = await DetectBlackIntervalsForCandidatesOrEmptyAsync(episode, candidates, threshold, minimumDuration, cancellationToken).ConfigureAwait(false);",
 '            blackIntervals = Environment.GetEnvironmentVariable("ABLATE_INTERVAL") == "1" ? [] : await DetectBlackIntervalsForCandidatesOrEmptyAsync(episode, candidates, threshold, minimumDuration, cancellationToken).ConfigureAwait(false);')
s=s.replace(
 "            blackIntervals = await DetectBlackIntervalsForCandidatesOrEmptyAsync(episode, scenes, threshold, minimumDuration, cancellationToken).ConfigureAwait(false);",
 '            blackIntervals = Environment.GetEnvironmentVariable("ABLATE_INTERVAL") == "1" ? [] : await DetectBlackIntervalsForCandidatesOrEmptyAsync(episode, scenes, threshold, minimumDuration, cancellationToken).ConfigureAwait(false);')
open(A,"w").write(s)

P="/home/intro-skipper/IntroSkipper/Analyzers/Credits/CreditDetectionPolicy.cs"
s=open(P).read()
s=s.replace(
 "        if (validDensities.Count < MinimumAdaptiveDensitySampleCount)\n        {\n            return DefaultMinimumBlackFrameDensity;\n        }",
 '        if (validDensities.Count < MinimumAdaptiveDensitySampleCount || Environment.GetEnvironmentVariable("ABLATE_ADAPTIVE") == "1")\n        {\n            return DefaultMinimumBlackFrameDensity;\n        }')
open(P,"w").write(s)
print("gates injected")
PY

echo "building once..."
dotnet build runner -c Release -p:SkipWebBuild=true >/dev/null 2>&1 || { echo "BUILD FAILED"; exit 2; }

run_variant () {
  local name="$1"; shift
  echo "================ $name ================"
  env "$@" dotnet run --project runner --no-build -c Release -- corpus/labels.csv corpus/clips "theory3_refine/ablation/ablation_$name.csv" >/dev/null 2>&1
  python3 score.py "theory3_refine/ablation/ablation_$name.csv" --tol 2,5 --json "$ABL/$name.json" | sed -n '/-- summary --/,$p'
  echo
}

run_variant full
run_variant no_boundary  ABLATE_BOUNDARY=1
run_variant no_adaptive  ABLATE_ADAPTIVE=1
run_variant no_interval  ABLATE_INTERVAL=1
run_variant core_only    ABLATE_BOUNDARY=1 ABLATE_ADAPTIVE=1 ABLATE_INTERVAL=1

git -C "$REPO" checkout -- "$ANALYZER" "$POLICY"
echo "reverted; tree status:"
git -C "$REPO" status --short -- "$ANALYZER" "$POLICY"
