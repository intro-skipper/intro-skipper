using System.Text.Json.Serialization;

namespace SegmentScanner.Contracts;

public enum CreditInferenceBackend { Cpu, OpenClGpu }
public enum CreditVideoDecoder { Cpu = 0, NvidiaNvdec, IntelQuickSync }
public enum CreditsDetectionMethod { TextAware = 0, IntroSkipper = 1 }

public sealed record PathMapping
{
    public string Id { get; init; } = "";
    public required string ServerPrefix { get; init; }
    public required string WorkerPrefix { get; init; }
    public bool? CaseSensitive { get; init; }
}

public sealed record WorkerPaths
{
    public string StateDirectory { get; init; } = "state";
    public string CacheDirectory { get; init; } = "cache";
    public string Ffmpeg { get; init; } = "ffmpeg";
    public string Ffprobe { get; init; } = "ffprobe";
    public string Python { get; init; } = "python";
    public string InferenceScript { get; init; } = "../inference/worker.py";
    public List<PathMapping> Mappings { get; init; } = [];
    public List<string> AllowedMediaRoots { get; init; } = [];
}

public sealed record WorkerResources
{
    public int ConcurrentJobs { get; init; } = 1;
    public int ConcurrentFiles { get; init; } = 2;
    public int FfmpegThreads { get; init; } = 2;
    public int InferenceThreads { get; init; } = 2;
    public double MaximumTemporaryGigabytes { get; init; } = 10;
    public int ProcessTimeoutSeconds { get; init; } = 1800;
    public int InferenceTimeoutSeconds { get; init; } = 120;
    public int MaximumCacheMegabytes { get; init; } = 4096;
}

public sealed record CreditDecisionProfile
{
    public string Id { get; init; } = "plex-observed-experimental";
    public string FeatureVersion { get; init; } = "1";
    public bool ProductionApproved { get; init; }
    public string? EvaluationReportHash { get; init; }
    public int MinimumPositiveSamples { get; init; } = 4;
    public double MinimumSpanSeconds { get; init; } = 6;
    public double MaximumBlankGapSeconds { get; init; } = 2;
    public double MinimumTextFraction { get; init; } = 0.002;
    public double MinimumTextWidth { get; init; } = 0.2;
    public double MinimumTextHeight { get; init; } = 0.08;
    public double SubtitleBandStart { get; init; } = 0.78;
    public double NearBlackRatio { get; init; } = 0.98;
    public double StaticDifferenceThreshold { get; init; } = 1;
}

public sealed record CreditsConfiguration
{
    [JsonIgnore]
    public bool UseGpu { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public CreditsDetectionMethod Method { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public CreditInferenceBackend InferenceBackend { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public CreditVideoDecoder VideoDecoder { get; init; }
    public string ProfileId { get; init; } = "plex-observed-experimental";
    public string? ModelManifest { get; init; }
    public double SampleIntervalSeconds { get; init; } = 2;
    public List<double> LookbackFractions { get; init; } = [0.1, 0.15, 0.2];
    public double RefinementFramesPerSecond { get; init; } = 4;
    public double StartOffsetSeconds { get; init; }
    public double EndOffsetSeconds { get; init; }
    public bool BlackOnlyFallback { get; init; }
    public CreditDecisionProfile DecisionProfile { get; init; } = new();
}

public sealed record ExportConfiguration
{
    public bool Enabled { get; init; }
    public string Writer { get; init; } = "Worker";
    public string XmlLayout { get; init; } = "MsChapterCompatible";
    public bool JsonSidecar { get; init; } = true;
    public bool PreserveUnknownFiles { get; init; } = true;
    public bool OverwriteModifiedOwnedFiles { get; init; }
    public string? MirroredRoot { get; init; }
}

public sealed record ServerConfiguration
{
    public required string BaseUrl { get; init; }
    public string CredentialEnvironmentVariable { get; init; } = "SEGMENTSCANNER_JELLYFIN_TOKEN";
    public bool RequireTlsValidation { get; init; } = true;
    public bool AllowInsecureHttp { get; init; }
    public string WorkerId { get; init; } = Environment.MachineName;
    public NodeSelectionConfiguration Node { get; init; } = new();
    public int PollSeconds { get; init; } = 30;
    public int HeartbeatSeconds { get; init; } = 30;
}

public sealed record WorkerConfiguration
{
    public int SchemaVersion { get; init; } = 1;
    public string Mode { get; init; } = "Offline";
    public WorkerPaths Paths { get; init; } = new();
    public WorkerResources Resources { get; init; } = new();
    public CreditsConfiguration Credits { get; init; } = new();
    public ExportConfiguration Exports { get; init; } = new();
    public ServerConfiguration? Server { get; init; }

    public void Validate()
    {
        ContractValidation.Require(SchemaVersion == 1 && Mode is "Offline" or "ConnectedWorker", "Unsupported configuration version/mode.");
        ContractValidation.Require(Resources.ConcurrentJobs == 1 && Resources.ConcurrentFiles is 1 or 2 or 4, "Version 1 supports one job and one, two or four files at a time.");
        ContractValidation.Require(Resources.FfmpegThreads is >= 1 and <= 32 && Resources.InferenceThreads is >= 1 and <= 32
            && Resources.ProcessTimeoutSeconds > 0 && Resources.InferenceTimeoutSeconds > 0 && Resources.MaximumTemporaryGigabytes > 0
            && double.IsFinite(Resources.MaximumTemporaryGigabytes) && Resources.MaximumCacheMegabytes > 0, "Invalid process/storage limits.");
        ContractValidation.Require(Enum.IsDefined(Credits.Method), "Unsupported credits detection method.");
        ContractValidation.Require(Enum.IsDefined(Credits.InferenceBackend), "Unsupported credits inference backend.");
        ContractValidation.Require(Enum.IsDefined(Credits.VideoDecoder), "Unsupported credits video decoder.");
        ContractValidation.Require(!Credits.BlackOnlyFallback, "Black-only detection is not an approved fallback.");
        ContractValidation.Require(double.IsFinite(Credits.SampleIntervalSeconds) && Credits.SampleIntervalSeconds > 0
            && Credits.LookbackFractions.Count is > 0 and <= 10 && Credits.LookbackFractions.All(f => double.IsFinite(f) && f > 0 && f <= 1)
            && Credits.LookbackFractions.SequenceEqual(Credits.LookbackFractions.Distinct().Order())
            && double.IsFinite(Credits.RefinementFramesPerSecond) && Credits.RefinementFramesPerSecond > 0
            && double.IsFinite(Credits.StartOffsetSeconds) && double.IsFinite(Credits.EndOffsetSeconds), "Invalid credits search/refinement policy.");
        var profile = Credits.DecisionProfile;
        ContractValidation.Require(profile.Id == Credits.ProfileId && profile.MinimumPositiveSamples >= 2 && profile.MinimumSpanSeconds > 0
            && profile.MaximumBlankGapSeconds >= 0 && profile.MinimumTextFraction is > 0 and <= 1
            && profile.MinimumTextWidth is > 0 and <= 1 && profile.MinimumTextHeight is > 0 and <= 1
            && profile.SubtitleBandStart is > 0 and <= 1 && profile.NearBlackRatio is > 0 and <= 1
            && profile.StaticDifferenceThreshold >= 0 && double.IsFinite(profile.StaticDifferenceThreshold)
            && double.IsFinite(profile.MinimumSpanSeconds) && double.IsFinite(profile.MaximumBlankGapSeconds), "Invalid decision profile.");
        ContractValidation.Require(!profile.ProductionApproved || (profile.Id == "text-credits-v1" && profile.EvaluationReportHash?.Length == 64), "Production profile requires a recorded evaluation report hash.");
        ContractValidation.Require(Exports.Writer is "Worker" or "MsChapter" && Exports.XmlLayout is "MsChapterCompatible" or "Adjacent"
            && Exports.PreserveUnknownFiles && !Exports.OverwriteModifiedOwnedFiles, "Unsafe or unsupported export policy.");
        if (Mode == "ConnectedWorker") ContractValidation.Require(Server is not null, "Connected worker requires server settings.");
        if (Server is { } server)
        {
            ContractValidation.Require(Uri.TryCreate(server.BaseUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment), "Invalid server URL.");
            ContractValidation.Require(server.RequireTlsValidation && (uri!.Scheme == "https" || server.AllowInsecureHttp), "TLS validation is required; HTTP needs an explicit override.");
            ContractValidation.Require(server.PollSeconds >= 1 && server.HeartbeatSeconds >= 1 && !string.IsNullOrWhiteSpace(server.CredentialEnvironmentVariable), "Invalid worker polling/credential settings.");
            NodeScope.ValidateSelection(server.Node);
        }
    }
}
