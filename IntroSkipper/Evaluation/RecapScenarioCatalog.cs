// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Subtitles;

namespace IntroSkipper.Evaluation;

/// <summary>
/// The round-2 SYNTHETIC-REPRESENTATIVE recap dataset, authored in code so it is the single source
/// of truth (the committed <c>R2-scenarios.json</c> is a serialized snapshot of this catalog). It
/// spans all four source shapes plus realistic per-tier inputs: chapter markers, subtitle cues
/// (English "Previously on" openers plus mid-dialogue distractors that must NOT match),
/// shared-sting presence/absence (including recurring themes and short idents that stress the
/// false-positive guard), and black-frame structure. These numbers exercise the real detection
/// logic; they DO NOT prove real-world accuracy (see <c>docs/recap-research/R2-integration-measurement.md</c>).
/// </summary>
internal static class RecapScenarioCatalog
{
    /// <summary>
    /// Gets the default catalog of labeled scenarios.
    /// </summary>
    public static IReadOnlyList<RecapScenario> Default { get; } = Build();

    /// <summary>
    /// Gets the default catalog wrapped in a serializable <see cref="RecapScenarioSet"/>.
    /// </summary>
    /// <returns>The scenario set.</returns>
    public static RecapScenarioSet ToSet()
    {
        var set = new RecapScenarioSet();
        foreach (var scenario in Default)
        {
            set.Scenarios.Add(scenario);
        }

        return set;
    }

    private static SubtitleCue Cue(double start, double end, string text) => new(start, end, text);

    private static IReadOnlyList<RecapScenario> Build() =>
    [
        // ===== Marble Court (legal drama): RecapFirst-dominant, shared "Previously on" sting, text subs =====
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Marble Court", Season = 3, Episode = 1, HasRecap = false, SourceShape = RecapSourceShape.NoRecap, Notes = "Season premiere, no recap. Clean control." },
            Inputs = new RecapEpisodeInputs { Duration = 2700, IntroDetected = true, IntroStart = 40 },
        },
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Marble Court", Season = 3, Episode = 2, HasRecap = true, RecapStart = 0, RecapEnd = 30, SourceShape = RecapSourceShape.RecapFirst, Notes = "Recap opens the episode; shared sting + montage fade at 30. Happy path." },
            Inputs = new RecapEpisodeInputs
            {
                Duration = 2700, IntroDetected = true, IntroStart = 95,
                StingPresent = true, StingStart = 0.5, StingEnd = 4,
                BlackFrameTimes = { 30 },
                SubtitleCues =
                {
                    Cue(1, 4, "Previously on Marble Court..."),
                    Cue(6, 11, "The verdict came in against us."),
                    Cue(14, 20, "We are going to appeal this decision."),
                    Cue(24, 29, "She lost everything that night."),
                },
            },
        },
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Marble Court", Season = 3, Episode = 3, HasRecap = true, RecapStart = 0, RecapEnd = 24, SourceShape = RecapSourceShape.RecapFirst, Notes = "Short recap-first, shared sting + fade at 24." },
            Inputs = new RecapEpisodeInputs
            {
                Duration = 2650, IntroDetected = true, IntroStart = 80,
                StingPresent = true, StingStart = 1, StingEnd = 4,
                BlackFrameTimes = { 24 },
                SubtitleCues =
                {
                    Cue(1, 4, "Previously on Marble Court..."),
                    Cue(7, 13, "The witness changed her story."),
                    Cue(17, 23, "That changes the whole case."),
                },
            },
        },
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Marble Court", Season = 3, Episode = 4, HasRecap = true, RecapStart = 0, RecapEnd = 36, SourceShape = RecapSourceShape.RecapFirst, Notes = "Recap-first WITH an explicit chapter marker [0,36]; chapter tier should win in every config." },
            Inputs = new RecapEpisodeInputs
            {
                Duration = 2700, IntroDetected = true, IntroStart = 100,
                HasChapterRecap = true, ChapterRecapStart = 0, ChapterRecapEnd = 36,
                StingPresent = true, StingStart = 0.5, StingEnd = 4,
                BlackFrameTimes = { 36 },
                SubtitleCues =
                {
                    Cue(1, 4, "Previously on Marble Court..."),
                    Cue(8, 14, "The partners want him gone."),
                    Cue(28, 34, "This is bigger than all of us."),
                },
            },
        },
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Marble Court", Season = 3, Episode = 5, HasRecap = false, SourceShape = RecapSourceShape.NoRecap, Notes = "Bottle episode, no recap. Clean control." },
            Inputs = new RecapEpisodeInputs { Duration = 2680, IntroDetected = true, IntroStart = 38 },
        },
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Marble Court", Season = 3, Episode = 6, HasRecap = true, RecapStart = 0, RecapEnd = 28, SourceShape = RecapSourceShape.RecapFirst, Notes = "Recap-first but only IMAGE subtitles (no cues): subtitle tier must abstain and fall back to the sting." },
            Inputs = new RecapEpisodeInputs
            {
                Duration = 2700, IntroDetected = true, IntroStart = 70,
                StingPresent = true, StingStart = 0.5, StingEnd = 4,
                BlackFrameTimes = { 28 },
            },
        },
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Marble Court", Season = 3, Episode = 7, HasRecap = true, RecapStart = 58, RecapEnd = 92, SourceShape = RecapSourceShape.ColdOpenThenRecap, Notes = "Cold open to 58, then recap to 92; shared sting + fades. Defeats start-forced-to-0." },
            Inputs = new RecapEpisodeInputs
            {
                Duration = 2700, IntroDetected = true, IntroStart = 96,
                StingPresent = true, StingStart = 58, StingEnd = 62,
                BlackFrameTimes = { 58, 92 },
                SubtitleCues =
                {
                    Cue(59, 62, "Previously on Marble Court..."),
                    Cue(65, 71, "The settlement fell apart."),
                    Cue(74, 80, "Both sides are out for blood."),
                    Cue(84, 90, "Nobody walks away clean."),
                },
            },
        },
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Marble Court", Season = 3, Episode = 8, HasRecap = true, RecapStart = 0, RecapEnd = 33, SourceShape = RecapSourceShape.RecapFirst, Notes = "Recap-first, shared sting + fade at 33." },
            Inputs = new RecapEpisodeInputs
            {
                Duration = 2700, IntroDetected = true, IntroStart = 88,
                StingPresent = true, StingStart = 0.5, StingEnd = 4,
                BlackFrameTimes = { 33 },
                SubtitleCues =
                {
                    Cue(1, 4, "Previously on Marble Court..."),
                    Cue(8, 14, "The judge recused herself."),
                    Cue(26, 32, "We start over from nothing."),
                },
            },
        },
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Marble Court", Season = 3, Episode = 9, HasRecap = false, SourceShape = RecapSourceShape.NoRecap, Notes = "No recap this week. Clean control." },
            Inputs = new RecapEpisodeInputs { Duration = 2700, IntroDetected = true, IntroStart = 41 },
        },
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Marble Court", Season = 3, Episode = 10, HasRecap = true, RecapStart = 53, RecapEnd = 89, SourceShape = RecapSourceShape.ColdOpenThenRecap, Notes = "Cold open to 53, recap to 89; shared sting + fades; no chapter." },
            Inputs = new RecapEpisodeInputs
            {
                Duration = 2700, IntroDetected = true, IntroStart = 94,
                StingPresent = true, StingStart = 53, StingEnd = 57,
                BlackFrameTimes = { 53, 89 },
                SubtitleCues =
                {
                    Cue(54, 57, "Previously on Marble Court..."),
                    Cue(60, 66, "The firm is under investigation."),
                    Cue(70, 76, "Someone has been leaking files."),
                    Cue(81, 87, "Trust no one in that building."),
                },
            },
        },

        // ===== Tidewater (procedural): ColdOpenThenRecap-dominant, shared sting, text subs =====
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Tidewater", Season = 2, Episode = 1, HasRecap = false, SourceShape = RecapSourceShape.NoRecap, Notes = "Premiere, cold open only, no recap. Clean control." },
            Inputs = new RecapEpisodeInputs { Duration = 2600, IntroDetected = true, IntroStart = 55 },
        },
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Tidewater", Season = 2, Episode = 2, HasRecap = true, RecapStart = 52, RecapEnd = 88, SourceShape = RecapSourceShape.ColdOpenThenRecap, Notes = "Cold open to 52, recap to 88; shared sting; fades at 52 and 88." },
            Inputs = new RecapEpisodeInputs
            {
                Duration = 2600, IntroDetected = true, IntroStart = 92,
                StingPresent = true, StingStart = 52, StingEnd = 56,
                BlackFrameTimes = { 52, 88 },
                SubtitleCues =
                {
                    Cue(53, 56, "Previously on Tidewater..."),
                    Cue(58, 63, "The body washed up at dawn."),
                    Cue(66, 72, "The detective knew the victim."),
                    Cue(75, 81, "This case is personal now."),
                    Cue(83, 87, "Everyone is a suspect."),
                },
            },
        },
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Tidewater", Season = 2, Episode = 3, HasRecap = true, RecapStart = 47, RecapEnd = 79, SourceShape = RecapSourceShape.ColdOpenThenRecap, Notes = "Cold open to 47, recap to 79; shared sting; fades on both sides." },
            Inputs = new RecapEpisodeInputs
            {
                Duration = 2600, IntroDetected = true, IntroStart = 84,
                StingPresent = true, StingStart = 47, StingEnd = 51,
                BlackFrameTimes = { 47, 79 },
                SubtitleCues =
                {
                    Cue(48, 51, "Previously on Tidewater..."),
                    Cue(54, 60, "The lab results came back."),
                    Cue(63, 69, "It was never an accident."),
                    Cue(73, 78, "We have been chasing a ghost."),
                },
            },
        },
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Tidewater", Season = 2, Episode = 4, HasRecap = true, RecapStart = 60, RecapEnd = 96, SourceShape = RecapSourceShape.ColdOpenThenRecap, Notes = "Long cold open to 60, recap to 96; shared sting; fades." },
            Inputs = new RecapEpisodeInputs
            {
                Duration = 2600, IntroDetected = true, IntroStart = 100,
                StingPresent = true, StingStart = 60, StingEnd = 64,
                BlackFrameTimes = { 60, 96 },
                SubtitleCues =
                {
                    Cue(61, 64, "Previously on Tidewater..."),
                    Cue(67, 73, "The mayor made a deal."),
                    Cue(77, 83, "Internal affairs is watching."),
                    Cue(88, 94, "Hand over the badge."),
                },
            },
        },
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Tidewater", Season = 2, Episode = 5, HasRecap = true, RecapStart = 44, RecapEnd = 82, SourceShape = RecapSourceShape.ColdOpenThenRecap, Notes = "Cold open to 44, recap to 82, WITH explicit chapter [44,82]; chapter should win in every config." },
            Inputs = new RecapEpisodeInputs
            {
                Duration = 2600, IntroDetected = true, IntroStart = 88,
                HasChapterRecap = true, ChapterRecapStart = 44, ChapterRecapEnd = 82,
                StingPresent = true, StingStart = 44, StingEnd = 48,
                BlackFrameTimes = { 44, 82 },
                SubtitleCues =
                {
                    Cue(45, 48, "Previously on Tidewater..."),
                    Cue(52, 58, "The harbor master vanished."),
                    Cue(74, 80, "Follow the money."),
                },
            },
        },
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Tidewater", Season = 2, Episode = 6, HasRecap = true, RecapStart = 55, RecapEnd = 90, SourceShape = RecapSourceShape.ColdOpenThenRecap, Notes = "Cold open to 55, recap to 90; shared sting but NO subtitles and NO chapter: only the hardened sting can fix the start." },
            Inputs = new RecapEpisodeInputs
            {
                Duration = 2600, IntroDetected = true, IntroStart = 95,
                StingPresent = true, StingStart = 55, StingEnd = 59,
                BlackFrameTimes = { 55, 90 },
            },
        },
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Tidewater", Season = 2, Episode = 7, HasRecap = true, RecapStart = 50, RecapEnd = 86, SourceShape = RecapSourceShape.ColdOpenThenRecap, Notes = "Cold open to 50, recap to 86; UNIQUE recap (no shared sting) but text subs: only the subtitle tier can see it." },
            Inputs = new RecapEpisodeInputs
            {
                Duration = 2600, IntroDetected = true, IntroStart = 90,
                BlackFrameTimes = { 50, 86 },
                SubtitleCues =
                {
                    Cue(51, 54, "Previously on Tidewater..."),
                    Cue(57, 63, "The witness recanted everything."),
                    Cue(67, 73, "We are back to square one."),
                    Cue(78, 84, "Someone wanted him silenced."),
                },
            },
        },
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Tidewater", Season = 2, Episode = 8, HasRecap = false, SourceShape = RecapSourceShape.NoRecap, Notes = "No recap; a recurring 24 s theme sting + a fade, NO intro detected: stresses the false-positive guard." },
            Inputs = new RecapEpisodeInputs
            {
                Duration = 2600, IntroDetected = false,
                StingPresent = true, StingStart = 30, StingEnd = 54,
                BlackFrameTimes = { 54 },
            },
        },
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Tidewater", Season = 2, Episode = 9, HasRecap = true, RecapStart = 49, RecapEnd = 85, SourceShape = RecapSourceShape.ColdOpenThenRecap, Notes = "Cold open to 49, recap to 85; NO sting, NO subtitles, NO chapter: no signal reaches it (honest miss for all)." },
            Inputs = new RecapEpisodeInputs
            {
                Duration = 2600, IntroDetected = true, IntroStart = 90,
                BlackFrameTimes = { 49, 85 },
            },
        },
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Tidewater", Season = 2, Episode = 10, HasRecap = true, RecapStart = 0, RecapEnd = 22, SourceShape = RecapSourceShape.RecapFirst, Notes = "Occasional recap-first; shared sting + fade at 22." },
            Inputs = new RecapEpisodeInputs
            {
                Duration = 2600, IntroDetected = true, IntroStart = 60,
                StingPresent = true, StingStart = 0.5, StingEnd = 3.5,
                BlackFrameTimes = { 22 },
                SubtitleCues =
                {
                    Cue(1, 4, "Previously on Tidewater..."),
                    Cue(8, 14, "The chief pulled the plug."),
                    Cue(16, 21, "We are on our own now."),
                },
            },
        },

        // ===== Starfall Saga (anime): AfterIntro (recap after the OP), text subs, sting often unreachable =====
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Starfall Saga", Season = 1, Episode = 2, HasRecap = true, RecapStart = 128, RecapEnd = 160, SourceShape = RecapSourceShape.AfterIntro, Notes = "Recap AFTER the OP (intro at 5). The intro-clamped window cannot reach it; only subtitles/chapters can." },
            Inputs = new RecapEpisodeInputs
            {
                Duration = 1500, IntroDetected = true, IntroStart = 5,
                StingPresent = true, StingStart = 128, StingEnd = 132,
                BlackFrameTimes = { 128, 160 },
                SubtitleCues =
                {
                    Cue(129, 132, "Previously on Starfall Saga..."),
                    Cue(135, 141, "The sky split open above the city."),
                    Cue(145, 151, "Only she can close the rift."),
                    Cue(154, 159, "Time is running out."),
                },
            },
        },
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Starfall Saga", Season = 1, Episode = 3, HasRecap = true, RecapStart = 102, RecapEnd = 140, SourceShape = RecapSourceShape.AfterIntro, Notes = "Recap after a 90 s OP (intro at 8). Subtitle/chapter only." },
            Inputs = new RecapEpisodeInputs
            {
                Duration = 1500, IntroDetected = true, IntroStart = 8,
                StingPresent = true, StingStart = 102, StingEnd = 106,
                BlackFrameTimes = { 102, 140 },
                SubtitleCues =
                {
                    Cue(103, 106, "Previously on Starfall Saga..."),
                    Cue(109, 115, "The guardians fell one by one."),
                    Cue(120, 126, "He carries the last ember."),
                    Cue(132, 138, "The throne must not fall."),
                },
            },
        },
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Starfall Saga", Season = 1, Episode = 4, HasRecap = true, RecapStart = 95, RecapEnd = 128, SourceShape = RecapSourceShape.AfterIntro, Notes = "Recap after the OP (intro at 6) but NO subtitles: structurally unreachable by every signal here (honest miss for all)." },
            Inputs = new RecapEpisodeInputs
            {
                Duration = 1500, IntroDetected = true, IntroStart = 6,
                StingPresent = true, StingStart = 95, StingEnd = 99,
                BlackFrameTimes = { 95, 128 },
            },
        },
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Starfall Saga", Season = 1, Episode = 5, HasRecap = true, RecapStart = 0, RecapEnd = 30, SourceShape = RecapSourceShape.RecapFirst, Notes = "This week the recap opens the episode; shared sting + fade at 30." },
            Inputs = new RecapEpisodeInputs
            {
                Duration = 1500, IntroDetected = true, IntroStart = 92,
                StingPresent = true, StingStart = 0.5, StingEnd = 4,
                BlackFrameTimes = { 30 },
                SubtitleCues =
                {
                    Cue(1, 4, "Previously on Starfall Saga..."),
                    Cue(8, 14, "The eclipse marked the beginning."),
                    Cue(24, 29, "Nothing would be the same."),
                },
            },
        },
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Starfall Saga", Season = 1, Episode = 6, HasRecap = false, SourceShape = RecapSourceShape.NoRecap, Notes = "Straight into the OP, no recap. Clean control." },
            Inputs = new RecapEpisodeInputs { Duration = 1500, IntroDetected = true, IntroStart = 12 },
        },
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Starfall Saga", Season = 1, Episode = 7, HasRecap = true, RecapStart = 110, RecapEnd = 146, SourceShape = RecapSourceShape.AfterIntro, Notes = "Recap after the OP WITH an explicit chapter [110,146]; chapter reaches it in every config." },
            Inputs = new RecapEpisodeInputs
            {
                Duration = 1600, IntroDetected = true, IntroStart = 7,
                HasChapterRecap = true, ChapterRecapStart = 110, ChapterRecapEnd = 146,
                StingPresent = true, StingStart = 110, StingEnd = 114,
                BlackFrameTimes = { 110, 146 },
                SubtitleCues =
                {
                    Cue(111, 114, "Previously on Starfall Saga..."),
                    Cue(120, 126, "The seal was broken."),
                    Cue(138, 144, "Run while you still can."),
                },
            },
        },
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Starfall Saga", Season = 1, Episode = 8, HasRecap = true, RecapStart = 0, RecapEnd = 27, SourceShape = RecapSourceShape.RecapFirst, Notes = "Recap-first, UNIQUE recap (no shared sting) but text subs: subtitle tier carries recall the sting cannot." },
            Inputs = new RecapEpisodeInputs
            {
                Duration = 1500, IntroDetected = true, IntroStart = 80,
                BlackFrameTimes = { 27 },
                SubtitleCues =
                {
                    Cue(1, 4, "Previously on Starfall Saga..."),
                    Cue(8, 14, "The comet returned after a thousand years."),
                    Cue(20, 25, "And it brought something with it."),
                },
            },
        },
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Starfall Saga", Season = 1, Episode = 9, HasRecap = true, RecapStart = 120, RecapEnd = 150, SourceShape = RecapSourceShape.AfterIntro, Notes = "Recap after the OP (intro at 7); shared sting (unreachable) + text subs." },
            Inputs = new RecapEpisodeInputs
            {
                Duration = 1500, IntroDetected = true, IntroStart = 7,
                StingPresent = true, StingStart = 120, StingEnd = 124,
                BlackFrameTimes = { 120, 150 },
                SubtitleCues =
                {
                    Cue(121, 124, "Previously on Starfall Saga..."),
                    Cue(128, 134, "The council voted to retreat."),
                    Cue(140, 148, "She refused to abandon them."),
                },
            },
        },

        // ===== Quiet Lane (slice-of-life): NoRecap-dominant + distractors =====
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Quiet Lane", Season = 1, Episode = 1, HasRecap = false, SourceShape = RecapSourceShape.NoRecap, Notes = "No recap. Clean control." },
            Inputs = new RecapEpisodeInputs { Duration = 1450, IntroDetected = true, IntroStart = 18 },
        },
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Quiet Lane", Season = 1, Episode = 2, HasRecap = false, SourceShape = RecapSourceShape.NoRecap, Notes = "No recap. Clean control." },
            Inputs = new RecapEpisodeInputs { Duration = 1450, IntroDetected = true, IntroStart = 20 },
        },
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Quiet Lane", Season = 1, Episode = 3, HasRecap = false, SourceShape = RecapSourceShape.NoRecap, Notes = "No recap; a recurring 23 s theme sting + a fade, NO intro detected: baseline emits a false positive, the guard rejects it." },
            Inputs = new RecapEpisodeInputs
            {
                Duration = 1450, IntroDetected = false,
                StingPresent = true, StingStart = 25, StingEnd = 48,
                BlackFrameTimes = { 48 },
            },
        },
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Quiet Lane", Season = 1, Episode = 4, HasRecap = false, SourceShape = RecapSourceShape.NoRecap, Notes = "No recap; a SHORT 4 s studio ident + a later fade, NO intro: slips through even the hardened guard (documented ceiling)." },
            Inputs = new RecapEpisodeInputs
            {
                Duration = 1450, IntroDetected = false,
                StingPresent = true, StingStart = 2, StingEnd = 6,
                BlackFrameTimes = { 20 },
            },
        },
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Quiet Lane", Season = 1, Episode = 5, HasRecap = false, SourceShape = RecapSourceShape.NoRecap, Notes = "No recap; an incidental mid-dialogue 'previously' that the anchored matcher must NOT treat as a recap opener." },
            Inputs = new RecapEpisodeInputs
            {
                Duration = 1450, IntroDetected = true, IntroStart = 22,
                SubtitleCues =
                {
                    Cue(12, 18, "As I mentioned previously on the phone, dinner is at eight."),
                    Cue(30, 36, "Did you remember to water the plants?"),
                },
            },
        },
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Quiet Lane", Season = 1, Episode = 6, HasRecap = false, SourceShape = RecapSourceShape.NoRecap, Notes = "No recap. Clean control with text subtitles but no opener phrase." },
            Inputs = new RecapEpisodeInputs
            {
                Duration = 1450, IntroDetected = true, IntroStart = 19,
                SubtitleCues =
                {
                    Cue(8, 14, "Good morning. Sleep well?"),
                    Cue(20, 26, "I made too much coffee again."),
                },
            },
        },
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Quiet Lane", Season = 1, Episode = 7, HasRecap = false, SourceShape = RecapSourceShape.NoRecap, Notes = "No recap; a recurring 22 s theme sting + a fade, NO intro: another guard test." },
            Inputs = new RecapEpisodeInputs
            {
                Duration = 1450, IntroDetected = false,
                StingPresent = true, StingStart = 28, StingEnd = 50,
                BlackFrameTimes = { 50 },
            },
        },
        new RecapScenario
        {
            Label = new RecapLabel { Series = "Quiet Lane", Season = 1, Episode = 8, HasRecap = true, RecapStart = 0, RecapEnd = 30, SourceShape = RecapSourceShape.RecapFirst, Notes = "Recap-first WITH an explicit chapter [0,30] and no other signal: chapter carries it in every config." },
            Inputs = new RecapEpisodeInputs
            {
                Duration = 1450, IntroDetected = true, IntroStart = 60,
                HasChapterRecap = true, ChapterRecapStart = 0, ChapterRecapEnd = 30,
                BlackFrameTimes = { 30 },
            },
        },
    ];
}
