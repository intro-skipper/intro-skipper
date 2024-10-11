namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using IntroSkipper.Analyzers;
using IntroSkipper.Data;
using Microsoft.Extensions.Logging;
using Xunit;

public class TestBlackFrames
{
    [FactSkipFFmpegTests]
    public void TestBlackFrameDetection()
    {
        var range = 1e-5;

        var expected = new List<BlackFrame>();
        expected.AddRange(CreateFrameSequence(2.04, 3));
        expected.AddRange(CreateFrameSequence(5, 6));
        expected.AddRange(CreateFrameSequence(8, 9.96));

        var actual = FFmpegWrapper.DetectBlackFrames(QueueFile("rainbow.mp4"), new(0, 10), 85);

        for (var i = 0; i < expected.Count; i++)
        {
            var (e, a) = (expected[i], actual[i]);
            Assert.Equal(e.Percentage, a.Percentage);
            Assert.InRange(a.Time, e.Time - range, e.Time + range);
        }
    }

    [FactSkipFFmpegTests]
    public void TestEndCreditDetection()
    {
        // new strategy new range
        var range = 3;

        var analyzer = CreateBlackFrameAnalyzer();

        var episode = QueueFile("credits.mp4");
        episode.Duration = (int)new TimeSpan(0, 5, 30).TotalSeconds;

        var result = analyzer.AnalyzeMediaFile(episode, 240, 30, 85);
        Assert.NotNull(result);
        Assert.InRange(result.Start, 300 - range, 300 + range);
    }

    private static QueuedEpisode QueueFile(string path)
    {
        return new()
        {
            EpisodeId = Guid.NewGuid(),
            Name = path,
            Path = "../../../video/" + path
        };
    }

    private static BlackFrame[] CreateFrameSequence(double start, double end)
    {
        var frames = new List<BlackFrame>();

        for (var i = start; i < end; i += 0.04)
        {
            frames.Add(new(100, i));
        }

        return [.. frames];
    }

    private static BlackFrameAnalyzer CreateBlackFrameAnalyzer()
    {
        var logger = new LoggerFactory().CreateLogger<BlackFrameAnalyzer>();
        return new(logger);
    }
}
