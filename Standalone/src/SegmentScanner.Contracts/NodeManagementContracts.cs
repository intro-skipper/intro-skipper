using System.Text.Json;

namespace SegmentScanner.Contracts;

public sealed record RemoteNodePolicy
{
    public List<Guid> LibraryIds { get; init; } = [];
    public List<RemoteAnalysisMode> Modes { get; init; } = [RemoteAnalysisMode.Introduction, RemoteAnalysisMode.Credits, RemoteAnalysisMode.Recap, RemoteAnalysisMode.Preview, RemoteAnalysisMode.Commercial];
    public CreditsDetectionMethod CreditsMethod { get; init; }
    public bool UseGpu { get; init; }
    public int ConcurrentFiles { get; init; } = 2;
    public List<PathMapping> PathMappings { get; init; } = [];
    public bool AutomaticImports { get; init; }
    public int LeaseMinutes { get; init; } = 30;
}

public sealed record NodeCreditRecipe
{
    public required string Id { get; init; }
    public CreditsDetectionMethod Method { get; init; }
    public bool UseGpu { get; init; }
    public CreditInferenceBackend InferenceBackend { get; init; }
    public CreditVideoDecoder VideoDecoder { get; init; }
    public required string ProfileHash { get; init; }
}

public sealed record NodeGpuInformation(string Name, string? Driver = null, long? MemoryBytes = null);

public sealed record NodeHardwareInformation
{
    public string OperatingSystem { get; init; } = "";
    public string? CpuName { get; init; }
    public int LogicalProcessors { get; init; }
    public long? MemoryBytes { get; init; }
    public List<NodeGpuInformation> Gpus { get; init; } = [];
}

public sealed record NodeRuntimeInformation
{
    public string Version { get; init; } = "";
    public List<RemoteAnalysisMode> SupportedModes { get; init; } = [RemoteAnalysisMode.Introduction, RemoteAnalysisMode.Credits, RemoteAnalysisMode.Recap, RemoteAnalysisMode.Preview, RemoteAnalysisMode.Commercial];
    public NodeHardwareInformation Hardware { get; init; } = new();
    public List<NodeCreditRecipe> CreditsRecipes { get; init; } = [];
    public List<PathMapping> SuggestedPathMappings { get; init; } = [];
    public int SuggestedConcurrentFiles { get; init; } = 2;
    public string? SyncedSettingsHash { get; init; }
}

public sealed record NodeHello(string NodeId, string Name, NodeRuntimeInformation Runtime);
public sealed record NodePolicyUpdate(RemoteNodePolicy Policy, bool Enabled);
public sealed record NodeSettingsNotice(string NodeId, long Revision, string SettingsHash);
public sealed record NodeConfiguration(RemoteNodeInfo Node, JobScanSettings Settings, string SettingsHash);
public sealed record ContextSegment(RemoteAnalysisMode Mode, long StartTicks, long EndTicks);

public sealed record JobScanSettings
{
    public int Version { get; init; } = 1;
    public JsonElement Configuration { get; init; } = JsonSerializer.SerializeToElement(new { });
    public string Action { get; init; } = "Default";
}
