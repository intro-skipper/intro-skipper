// Standalone gold-baseline runner: drives the REAL CreditsBlackFrameAnalyzer over
// the synthetic corpus and writes predictions/baseline_current_csharp.csv.
//
// Usage (from tools/credit-research):
//   dotnet run --project runner -c Release -- corpus/labels.csv corpus/clips predictions/baseline_current_csharp.csv
using System.Globalization;
using IntroSkipper.Analyzers;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging.Abstractions;

if (args.Length < 3)
{
    Console.Error.WriteLine("args: <labels.csv> <clips-dir> <out.csv>");
    return 1;
}

string labelsPath = args[0], clipsDir = args[1], outPath = args[2];

// Plugin.Instance is null here; CreditsBlackFrameAnalyzer + FFmpegService both
// fall back to a default PluginConfiguration and the "ffmpeg" binary on PATH.
var cache = new DetectionCacheService(NullLogger<DetectionCacheService>.Instance);
var ffmpeg = new FFmpegService(NullLogger<FFmpegService>.Instance, cache);
var analyzer = new CreditsBlackFrameAnalyzer(NullLogger<CreditsBlackFrameAnalyzer>.Instance, ffmpeg);

const int minimumPercentage = 85, threshold = 28, minimumDuration = 15;

var lines = File.ReadAllLines(labelsPath);
var header = lines[0].Split(',');
int idIdx = Array.IndexOf(header, "id");
int durIdx = Array.IndexOf(header, "duration_s");

using var w = new StreamWriter(outPath);
w.WriteLine("id,predicted_start");

for (int i = 1; i < lines.Length; i++)
{
    var cols = lines[i].Split(',');
    if (cols.Length <= idIdx) continue;
    string id = cols[idIdx];
    double duration = double.Parse(cols[durIdx], CultureInfo.InvariantCulture);
    string clip = Path.Combine(clipsDir, id + ".mp4");
    if (!File.Exists(clip)) { Console.Error.WriteLine($"missing {clip}"); continue; }

    var episode = new QueuedEpisode
    {
        EpisodeId = Guid.NewGuid(),
        Name = id,
        Path = clip,
        Duration = duration,
        CreditsFingerprintStart = 0,
        CreditsFingerprintEnd = duration,
    };

    string pred = "";
    try
    {
        var seg = await analyzer.DetectCreditsAsync(episode, minimumPercentage, threshold, minimumDuration);
        if (seg is not null && seg.Valid)
            pred = seg.Start.ToString("F3", CultureInfo.InvariantCulture);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"{id}: {ex.GetType().Name} {ex.Message}");
    }

    w.WriteLine($"{id},{pred}");
    Console.WriteLine($"  {id,-26} -> {(pred == "" ? "none" : pred)}");
}

Console.WriteLine($"wrote {outPath}");
return 0;
