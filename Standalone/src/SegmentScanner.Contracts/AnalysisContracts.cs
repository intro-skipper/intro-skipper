using System.Text.Json;
using System.Text.Json.Serialization;

namespace SegmentScanner.Contracts;

public enum RemoteAnalysisMode { Introduction = 0, Credits = 1, Recap = 2, Preview = 3, Commercial = 4 }
public enum AnalysisStatus { CompletedWithSegments, CompletedNoSegments, NeedsReview, SkippedUnsupported, Failed, Cancelled, Interrupted }
public enum CandidateDecision { Accept, Review, Reject }
public enum ChapterOrigin { Authored, ScannerGenerated, UserReviewed, Unknown }

public sealed record ChapterSnapshot
{
    public required string Name { get; init; }
    public required long StartTicks { get; init; }
    public ChapterOrigin Origin { get; init; } = ChapterOrigin.Unknown;
    public string? OriginId { get; init; }
    public string? ExportRevision { get; init; }
}

public sealed record MediaSignature
{
    public required long Length { get; init; }
    public required long LastWriteUtcTicks { get; init; }
    public required long TimelineEndTicks { get; init; }
    public required string SampleHash { get; init; }
    public required string StreamLayoutHash { get; init; }
}

public sealed record JobItem
{
    public required Guid ItemId { get; init; }
    public required string MediaSourceKey { get; init; }
    public required string SourcePath { get; init; }
    public string? PathMapId { get; init; }
    public required string FileVersionToken { get; init; }
    public string ServerAnalysisConfigHash { get; init; } = "";
    public Dictionary<RemoteAnalysisMode, string> ExpectedModeRevisions { get; init; } = [];
    public string SeasonKey { get; init; } = "";
    public int Order { get; init; }
    public bool WriteTarget { get; init; } = true;
    public bool Multipart { get; init; }
    public long RuntimeTicks { get; init; }
    public MediaSignature? ExpectedSignature { get; init; }
    public List<ChapterSnapshot> Chapters { get; init; } = [];
    public bool ChaptersAreAuthoritative { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsMovie { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsAnime { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SeasonNumber { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? EpisodeNumber { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ContextSegment>? ContextSegments { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<RemoteAnalysisMode, string>? ContextRevisions { get; init; }
}

public sealed record JobSnapshot
{
    public int SchemaVersion { get; init; } = 1;
    public required Guid JobId { get; init; }
    public required Guid AttemptId { get; init; }
    public string? ServerId { get; init; }
    public List<RemoteAnalysisMode> Modes { get; init; } = [RemoteAnalysisMode.Introduction, RemoteAnalysisMode.Credits];
    public required List<JobItem> Items { get; init; }
    public JsonElement IntroductionSettings { get; init; } = JsonSerializer.SerializeToElement(new { });
    public string IntroductionProfileHash { get; init; } = "";
    public string CreditsProfileId { get; init; } = "plex-observed-experimental";
    public string CreditsProfileHash { get; init; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public CreditsDetectionMethod CreditsMethod { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? CreditsSettings { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JobScanSettings? ScanSettings { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RemoteNodePolicy? NodePolicy { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NodeCreditRecipe? CreditsRecipe { get; init; }
    public string? LeaseToken { get; init; }
    public int LeaseGeneration { get; init; }
    public DateTimeOffset? LeaseExpiresUtc { get; init; }
    public string? OfflineContextId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NodeId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? NodeRevision { get; init; }
}

public sealed record SegmentCandidate
{
    public required string CandidateId { get; init; }
    public required RemoteAnalysisMode Type { get; init; }
    public required long StartTicks { get; init; }
    public required long EndTicks { get; init; }
    public long? RawStartTicks { get; init; }
    public long? RawEndTicks { get; init; }
    public long? StartUncertaintyTicks { get; init; }
    public long? EndUncertaintyTicks { get; init; }
    public CandidateDecision Decision { get; init; } = CandidateDecision.Review;
    public string ConfidenceTier { get; init; } = "Review";
    public List<string> Reasons { get; init; } = [];
    public bool ReachesFileEnd { get; init; }
}

public sealed record TimeRange(long StartTicks, long EndTicks);

public sealed record DetectorIdentity
{
    public required string Id { get; init; }
    public string UpstreamCommit { get; init; } = "e39a56b74308bfef95f8d83b4ccdabc1f4529d8a";
    public required string ProfileHash { get; init; }
    public string? ModelSha256 { get; init; }
    public string? InferenceEnvironment { get; init; }
    public bool ProductionApproved { get; init; }
}

public sealed record AnalysisResult
{
    public int SchemaVersion { get; init; } = 1;
    public required Guid AnalysisId { get; init; }
    public required Guid JobId { get; init; }
    public required Guid AttemptId { get; init; }
    public string? ServerId { get; init; }
    public required Guid ItemId { get; init; }
    public required string MediaSourceKey { get; init; }
    public required string SourcePath { get; init; }
    public string? PathMapId { get; init; }
    public required string FileVersionToken { get; init; }
    public string ExpectedItemModeRevision { get; init; } = "0";
    public required RemoteAnalysisMode Mode { get; init; }
    public required AnalysisStatus Status { get; init; }
    public string BoundaryStage { get; init; } = "FinalAdjusted";
    public required long TimelineEndTicks { get; init; }
    public MediaSignature? MediaValidation { get; init; }
    public required DetectorIdentity Detector { get; init; }
    public List<TimeRange> SearchedRanges { get; init; } = [];
    public List<SegmentCandidate> Segments { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public Dictionary<string, double> Metrics { get; init; } = [];
}

public sealed record ResultBundle
{
    public int SchemaVersion { get; init; } = 1;
    public required List<AnalysisResult> Results { get; init; }
    public List<CanonicalResult> Accepted { get; init; } = [];
}

public sealed record CanonicalResult
{
    public required AnalysisResult Result { get; init; }
    public required long ResultRevision { get; init; }
    public bool Disabled { get; init; }
    public string Authority { get; init; } = "OfflineReview";
    public List<string> SuppressedCandidateIds { get; init; } = [];
}

public sealed record ReviewDecision
{
    public required Guid AnalysisId { get; init; }
    public required string CandidateId { get; init; }
    public required CandidateDecision Decision { get; init; }
    public long? StartTicks { get; init; }
    public long? EndTicks { get; init; }
}

public sealed record ReviewDocument
{
    public int SchemaVersion { get; init; } = 1;
    public required List<ReviewDecision> Decisions { get; init; }
    public List<Guid> AcceptNoSegments { get; init; } = [];
}

public sealed record RemoteCapabilities
{
    public int ProtocolVersion { get; init; } = 3;
    public required string ServerId { get; init; }
    public required string ServerVersion { get; init; }
    public required string PluginVersion { get; init; }
    public string AnalysisContract { get; init; } = "segment-scanner-v1";
    public int MaximumItems { get; init; } = 100;
    public List<string> ApprovedCreditsProfileHashes { get; init; } = [];
    public string SelectedCreditsProfileHash { get; init; } = "";
    public bool SupportsHeldResultReview { get; init; }
    public bool SupportsNodeConfiguration { get; init; }
}

public sealed record ClaimRequest(string WorkerId);
public sealed record LeaseRequest(string LeaseToken, int LeaseGeneration);
public sealed record SubmitRequest(string LeaseToken, int LeaseGeneration, List<AnalysisResult> Results);
public sealed record ImportRequest(string OfflineContextId, List<AnalysisResult> Results);
public sealed record RemoteReviewRequest(List<AnalysisResult> Results, ReviewDocument Review);
public sealed record ImportReceipt(Guid AnalysisId, Guid ItemId, RemoteAnalysisMode Mode, string Outcome, string? Reason, CanonicalResult? Current);
public sealed record RevisionRequest(List<Guid> ItemIds);
public sealed record RevisionResponse(Guid ItemId, RemoteAnalysisMode Mode, long Revision);

public static class ContractValidation
{
    public static void Job(JobSnapshot job)
    {
        Require(job.SchemaVersion == 1 && job.JobId != Guid.Empty && job.AttemptId != Guid.Empty, "Unsupported or missing job identity.");
        Require(Enum.IsDefined(job.CreditsMethod), "Unsupported credits detection method.");
        Require(job.Items.Count is > 0 and <= 100 && job.Modes.Count is > 0 and <= 5 && job.Modes.All(mode => Enum.IsDefined(mode)) && job.Modes.Distinct().Count() == job.Modes.Count, "Invalid item/mode count.");
        Require(job.Items.Select(i => i.ItemId).Distinct().Count() == job.Items.Count, "Duplicate item identity.");
        foreach (var item in job.Items)
        {
            Require(item.ItemId != Guid.Empty && !string.IsNullOrWhiteSpace(item.MediaSourceKey) && !string.IsNullOrWhiteSpace(item.SourcePath), "Incomplete media identity.");
            Require(IsFileVersionToken(item.FileVersionToken) && item.RuntimeTicks >= 0, "Invalid file-version token or runtime.");
            Require(item.ExpectedModeRevisions.Values.All(IsDecimalToken), "Invalid revision token.");
            Require(item.Chapters.All(c => c.StartTicks >= 0 && (item.RuntimeTicks == 0 || c.StartTicks < item.RuntimeTicks)), "Invalid chapter start.");
            Require(item.ExpectedModeRevisions.Keys.All(mode => Enum.IsDefined(mode)), "Unsupported revision mode.");
            Require(item.ContextSegments is null || (item.ContextSegments.Count <= 100 && item.ContextSegments.All(segment =>
                segment.Mode is RemoteAnalysisMode.Introduction or RemoteAnalysisMode.Credits && segment.StartTicks >= 0
                && segment.StartTicks < segment.EndTicks && (item.RuntimeTicks == 0 || segment.EndTicks <= item.RuntimeTicks))), "Invalid context segment.");
            Require(item.ContextRevisions is null || (item.ContextRevisions.Count <= 2 && item.ContextRevisions.All(context =>
                context.Key is RemoteAnalysisMode.Introduction or RemoteAnalysisMode.Credits && IsSha256(context.Value))), "Invalid context revision.");
        }

        if (job.ServerId is not null)
        {
            Require(job.Items.All(i => job.Modes.All(m => i.ExpectedModeRevisions.ContainsKey(m))), "Connected jobs require all expected mode revisions.");
            Require(!job.Modes.Contains(RemoteAnalysisMode.Credits) || IsSha256(job.CreditsProfileHash), "Connected credits jobs require a lowercase SHA-256 credits profile hash.");
            Require(!job.Modes.Contains(RemoteAnalysisMode.Credits) || job.CreditsMethod != CreditsDetectionMethod.IntroSkipper
                || job.CreditsSettings is { ValueKind: JsonValueKind.Object }, "Original credits jobs require their effective server settings.");
        }

        if (job.NodeId is not null)
        {
            Require(!string.IsNullOrWhiteSpace(job.NodeId) && job.NodeId.Length <= 200 && job.NodeRevision is > 0, "Node-scoped jobs require a bounded node id and positive node revision.");
        }
        else
        {
            Require(job.NodeRevision is null, "Legacy jobs cannot include a node revision.");
        }
    }

    public static void Result(AnalysisResult result)
    {
        Require(result.SchemaVersion == 1 && result.AnalysisId != Guid.Empty && result.ItemId != Guid.Empty, "Unsupported or missing result identity.");
        Require(Enum.IsDefined(result.Mode) && Enum.IsDefined(result.Status), "Unsupported result mode/status.");
        Require(IsFileVersionToken(result.FileVersionToken) && IsDecimalToken(result.ExpectedItemModeRevision), "Invalid result version/revision.");
        Require((result.TimelineEndTicks > 0 || result.Status is AnalysisStatus.Failed or AnalysisStatus.Cancelled or AnalysisStatus.Interrupted or AnalysisStatus.SkippedUnsupported)
            && result.BoundaryStage == "FinalAdjusted", "Result must use the validated final timeline.");
        Require(result.Segments.Count <= 100 && result.Segments.Select(s => s.CandidateId).Distinct().Count() == result.Segments.Count, "Invalid/duplicate candidates.");
        foreach (var segment in result.Segments)
        {
            Require(segment.Type == result.Mode && segment.StartTicks >= 0 && segment.StartTicks < segment.EndTicks && segment.EndTicks <= result.TimelineEndTicks, "Invalid segment boundary/type.");
        }

        var ordered = result.Segments.Where(s => s.Decision != CandidateDecision.Reject).OrderBy(s => s.StartTicks).ToList();
        Require(!ordered.Zip(ordered.Skip(1)).Any(pair => pair.First.EndTicks > pair.Second.StartTicks), "Segments overlap.");
        Require(result.Metrics.Values.All(double.IsFinite), "Non-finite metric.");
        Require(result.SearchedRanges.All(r => r.StartTicks >= 0 && r.StartTicks < r.EndTicks && r.EndTicks <= result.TimelineEndTicks), "Invalid searched range.");
        Require(result.Status != AnalysisStatus.CompletedNoSegments || (result.Segments.Count == 0 && result.SearchedRanges.Count > 0), "Negative result requires search coverage and no candidates.");
        Require(result.Status != AnalysisStatus.CompletedWithSegments || result.Segments.Any(s => s.Decision == CandidateDecision.Accept), "Completed result has no accepted candidates.");
        Require(result.Status != AnalysisStatus.CompletedWithSegments || result.Segments.All(s => s.Decision != CandidateDecision.Review), "Unresolved candidates require review.");
    }

    public static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static bool IsDecimalToken(string value) => long.TryParse(value, out var parsed) && parsed >= 0 && parsed.ToString(System.Globalization.CultureInfo.InvariantCulture) == value;
    public static bool IsFileVersionToken(string value)
        => long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            && parsed.ToString(System.Globalization.CultureInfo.InvariantCulture) == value;
    public static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
