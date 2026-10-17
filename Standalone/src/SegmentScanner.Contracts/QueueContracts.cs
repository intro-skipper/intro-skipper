namespace SegmentScanner.Contracts;

public sealed record ScannerQueueActivity(Guid ItemId, RemoteAnalysisMode Mode, string Phase);

public sealed record ScannerQueueProgressRequest(LeaseRequest Lease, List<ScannerQueueActivity> Active, List<Guid> Completed);

public sealed record ScannerQueueControlRequest(string Action);

public sealed record ScannerQueueItem(Guid ItemId, string Name, string? Series, string? Library);

public sealed record ScannerQueueJob(
    string Id,
    string NodeId,
    string NodeName,
    string State,
    string Kind,
    List<RemoteAnalysisMode> Modes,
    List<ScannerQueueItem> Items,
    List<ScannerQueueActivity> Active,
    List<Guid> Completed,
    DateTimeOffset? UpdatedUtc);

public sealed record ScannerQueueSnapshot(bool Paused, List<ScannerQueueJob> Running, List<ScannerQueueJob> Pending, int TotalPendingJobs);
