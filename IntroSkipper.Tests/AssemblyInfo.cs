using Xunit;

// The test suite uses process-wide static state (e.g., `Plugin.Instance`).
// Run tests sequentially to avoid cross-test interference.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
