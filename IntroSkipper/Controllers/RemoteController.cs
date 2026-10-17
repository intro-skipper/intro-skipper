// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Net.Mime;
using System.Text;
using IntroSkipper.Db;
using IntroSkipper.Remote;
using IntroSkipper.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SegmentScanner.Contracts;

namespace IntroSkipper.Controllers;

/// <summary>
/// Elevated server bridge for durable remote segment analysis work.
/// </summary>
/// <param name="database">Server-owned remote-job and admission facade.</param>
/// <param name="applicationHost">Jellyfin host used to expose the running target version.</param>
/// <param name="libraryManager">Jellyfin library catalog used to validate node declarations.</param>
/// <param name="notifier">Save notifications for connected nodes.</param>
/// <param name="scheduler">Queues dependent analysis after remote imports.</param>
[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
[Route("IntroSkipper/Remote/v2")]
[Route("IntroSkipper/Remote/v3")]
public sealed class RemoteController(IntroSkipperDatabase database, IServerApplicationHost applicationHost, ILibraryManager libraryManager, NodeSettingsNotifier? notifier = null, AnalysisScheduler? scheduler = null) : ControllerBase
{
    private const int MaximumItems = 100;
    private const int MaximumBodyBytes = 1024 * 1024;
    private readonly IntroSkipperDatabase _database = database;
    private readonly IServerApplicationHost _applicationHost = applicationHost;
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly NodeSettingsNotifier? _notifier = notifier;
    private readonly AnalysisScheduler? _scheduler = scheduler;

    /// <summary>
    /// Gets the server's supported remote-analysis protocol and approved credits profiles.
    /// </summary>
    /// <returns>The capability document.</returns>
    [HttpGet("capabilities")]
    [ProducesResponseType(typeof(RemoteCapabilities), StatusCodes.Status200OK)]
    public IActionResult GetCapabilities()
    {
        var plugin = Plugin.Instance!;
        return Wire(new RemoteCapabilities
        {
            ServerId = _applicationHost.SystemId,
            ServerVersion = _applicationHost.ApplicationVersionString,
            PluginVersion = plugin.Version.ToString(4),
            ApprovedCreditsProfileHashes = [],
            SelectedCreditsProfileHash = string.Empty,
            SupportsHeldResultReview = true,
            SupportsNodeConfiguration = true,
        });
    }

    /// <summary>
    /// Gets the current Jellyfin library catalog for standalone node registration.
    /// </summary>
    /// <returns>The selectable server libraries and their media roots.</returns>
    [HttpGet("libraries")]
    [ProducesResponseType(typeof(List<RemoteLibrary>), StatusCodes.Status200OK)]
    public IActionResult GetLibraries()
        => Wire(GetLibraryCatalog());

    /// <summary>
    /// Gets server-registered scanner nodes and their enablement state.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The current registered nodes.</returns>
    [HttpGet("nodes")]
    [ProducesResponseType(typeof(List<RemoteNodeInfo>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetNodes(CancellationToken cancellationToken)
    {
        var nodes = await _database.GetRemoteNodesAsync(cancellationToken).ConfigureAwait(false);
        var local = await _database.GetLocalNodeAsync(GetLibraryCatalog().Select(library => library.Id).ToArray(), cancellationToken).ConfigureAwait(false);
        return Wire(new[] { local }.Concat(nodes.Where(node => !node.IsLocal)).ToList());
    }

    /// <summary>Refreshes runtime capabilities and presence without changing server policy.</summary>
    /// <param name="cancellationToken">Request cancellation.</param>
    /// <returns>The authoritative node configuration.</returns>
    [HttpPost("nodes/hello")]
    public async Task<IActionResult> Hello(CancellationToken cancellationToken)
    {
        var read = await TryReadAsync<NodeHello>(cancellationToken).ConfigureAwait(false);
        if (read.Value is not { } hello || string.IsNullOrWhiteSpace(hello.NodeId) || hello.NodeId.Length > 200
            || hello.NodeId == IntroSkipperDatabase.LocalNodeId || string.IsNullOrWhiteSpace(hello.Name) || hello.Name.Length > 200
            || hello.Runtime is not { Hardware.Gpus: { } gpus, CreditsRecipes: { } recipes, SupportedModes: { } supportedModes, SuggestedPathMappings: { } mappings }
            || recipes.Count > 16 || gpus.Count > 32 || supportedModes.Count > 5 || mappings.Count > 32 || mappings.Any(mapping => mapping is null)
            || hello.Runtime.SupportedModes.Any(mode => !Enum.IsDefined(mode))
            || hello.Runtime.CreditsRecipes.Any(recipe => !ContractValidation.IsSha256(recipe.ProfileHash)
                || !Enum.IsDefined(recipe.Method) || !Enum.IsDefined(recipe.VideoDecoder) || !Enum.IsDefined(recipe.InferenceBackend)))
        {
            return BadRequest("Invalid node capabilities.");
        }

        try
        {
            foreach (var mapping in mappings)
            {
                NodeScope.NormalizeAbsolutePath(mapping.ServerPrefix);
                NodeScope.NormalizeAbsolutePath(mapping.WorkerPrefix);
            }
        }
        catch (InvalidDataException exception)
        {
            return BadRequest(exception.Message);
        }

        var previous = await _database.GetRemoteNodeAsync(hello.NodeId, cancellationToken).ConfigureAwait(false);
        var node = await _database.HelloRemoteNodeAsync(hello, cancellationToken).ConfigureAwait(false);
        if (previous is { Policy: null } && _scheduler is not null)
        {
            var pendingIds = await _database.GetPendingRemoteItemIdsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var itemId in pendingIds)
            {
                _ = _scheduler.ItemChangedAsync(itemId);
            }
        }

        return Wire(node);
    }

    /// <summary>Gets saved policy and shared analyzer settings after startup or an invalidation.</summary>
    /// <param name="nodeId">Stable node identity.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    /// <returns>An immutable settings snapshot.</returns>
    [HttpGet("nodes/{nodeId}/configuration")]
    public async Task<IActionResult> GetNodeConfiguration(string nodeId, CancellationToken cancellationToken)
    {
        var node = await _database.GetRemoteNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        if (node is null || node.IsLocal)
        {
            return NotFound();
        }

        var settings = RemoteAnalysisDispatcher.GetScanSettings(Plugin.Instance!.Configuration);
        return Wire(new NodeConfiguration(node, settings, WireJson.Hash(settings)));
    }

    /// <summary>Saves server-owned policy and then notifies connected nodes.</summary>
    /// <param name="nodeId">Stable node identity.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    /// <returns>The committed node.</returns>
    [HttpPut("nodes/{nodeId}/policy")]
    public async Task<IActionResult> SaveNodePolicy(string nodeId, CancellationToken cancellationToken)
    {
        var read = await TryReadAsync<NodePolicyUpdate>(cancellationToken).ConfigureAwait(false);
        if (read.Value is not { Policy: { } policy } update)
        {
            return BadRequest("Invalid node policy.");
        }

        try
        {
            var catalogIds = GetLibraryCatalog().Select(library => library.Id).ToHashSet();
            ContractValidation.Require(
                policy.LibraryIds.Count <= 100 && policy.LibraryIds.Distinct().Count() == policy.LibraryIds.Count && policy.LibraryIds.All(catalogIds.Contains),
                "Select current Jellyfin libraries.");
            ContractValidation.Require(
                policy.Modes.Count <= 5 && policy.Modes.All(mode => Enum.IsDefined(mode)) && policy.Modes.Distinct().Count() == policy.Modes.Count,
                "Select supported scan types.");
            ContractValidation.Require(
                Enum.IsDefined(policy.CreditsMethod) && policy.ConcurrentFiles is 1 or 2 or 4 && policy.LeaseMinutes is >= 1 and <= 120 && policy.PathMappings.Count <= 32,
                "Invalid node options.");
            foreach (var path in policy.PathMappings)
            {
                NodeScope.NormalizeAbsolutePath(path.ServerPrefix);
                NodeScope.NormalizeAbsolutePath(path.WorkerPrefix);
            }

            ContractValidation.Require(
                policy.PathMappings.Select(path => NodeScope.NormalizeAbsolutePath(path.ServerPrefix)).Distinct(StringComparer.Ordinal).Count() == policy.PathMappings.Count,
                "Duplicate path variables.");
            if (nodeId == IntroSkipperDatabase.LocalNodeId)
            {
                ContractValidation.Require(
                    policy.CreditsMethod == CreditsDetectionMethod.IntroSkipper && !policy.UseGpu && policy.PathMappings.Count == 0,
                    "The local node uses the plugin's built-in CPU methods and server media paths.");
            }
            else if (update.Enabled && policy.Modes.Contains(RemoteAnalysisMode.Credits))
            {
                var current = await _database.GetRemoteNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
                ContractValidation.Require(
                    current?.Runtime?.CreditsRecipes.Any(recipe => recipe.Method == policy.CreditsMethod && recipe.UseGpu == policy.UseGpu) == true,
                    "The node has no available recipe for this credits method and GPU choice. Reconnect it or choose CPU.");
            }

            var node = await _database.UpdateNodePolicyAsync(nodeId, update, cancellationToken).ConfigureAwait(false);
            if (node is null)
            {
                return NotFound();
            }

            _notifier?.Publish();
            return Wire(node);
        }
        catch (InvalidDataException exception)
        {
            return BadRequest(exception.Message);
        }
    }

    /// <summary>Streams save notifications; settings and secrets are never sent in events.</summary>
    /// <param name="nodeId">Stable node identity.</param>
    /// <param name="cancellationToken">Connection cancellation.</param>
    /// <returns>A streaming response until disconnect.</returns>
    [HttpGet("nodes/{nodeId}/events")]
    [Produces("text/event-stream")]
    public async Task Events(string nodeId, CancellationToken cancellationToken)
    {
        if (_notifier is null || await _database.GetRemoteNodeAsync(nodeId, cancellationToken).ConfigureAwait(false) is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, HttpContext.RequestAborted);
        var streamToken = connection.Token;
        var events = _notifier.ListenAsync(streamToken).GetAsyncEnumerator(streamToken);
        await using var streamLifetime = events.ConfigureAwait(false);
        var pending = events.MoveNextAsync().AsTask();
        try
        {
            while (!streamToken.IsCancellationRequested)
            {
                var ready = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(20), streamToken)).ConfigureAwait(false);
                if (ready == pending)
                {
                    if (!await pending.ConfigureAwait(false))
                    {
                        break;
                    }

                    await Response.WriteAsync("event: settings\ndata: changed\n\n", streamToken).ConfigureAwait(false);
                    pending = events.MoveNextAsync().AsTask();
                }
                else
                {
                    await Response.WriteAsync(": heartbeat\n\n", streamToken).ConfigureAwait(false);
                }

                await Response.Body.FlushAsync(streamToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (streamToken.IsCancellationRequested)
        {
            // A disconnected stream owns cancellation; no pending read may outlive disposal.
        }
        finally
        {
            await connection.CancelAsync().ConfigureAwait(false);
            try
            {
                await pending.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (streamToken.IsCancellationRequested)
            {
            }
        }
    }

    /// <summary>
    /// Registers or refreshes a standalone-owned node declaration without enabling it.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted node declaration.</returns>
    [HttpPost("nodes/register")]
    [ProducesResponseType(typeof(RemoteNodeInfo), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult RegisterNode(CancellationToken cancellationToken)
        => StatusCode(StatusCodes.Status409Conflict, "Upgrade to IntroSkipper-Node protocol 3; node policy is managed on the server.");

    /// <summary>
    /// Changes only a registered node's server-controlled availability.
    /// </summary>
    /// <param name="nodeId">Stable standalone node identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated node declaration or 404.</returns>
    [HttpPost("nodes/{nodeId}/enabled")]
    [ProducesResponseType(typeof(RemoteNodeInfo), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetNodeEnabled([FromRoute] string nodeId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(nodeId) || nodeId.Length > 200)
        {
            return BadRequest("A bounded node id is required.");
        }

        var read = await TryReadAsync<NodeEnabledRequest>(cancellationToken).ConfigureAwait(false);
        if (read.TimedOut)
        {
            return StatusCode(StatusCodes.Status408RequestTimeout, "Timed out reading the request body.");
        }

        if (read.Value is null)
        {
            return BadRequest("Invalid node enablement request.");
        }

        var node = await _database.SetRemoteNodeEnabledAsync(nodeId, read.Value.Enabled, cancellationToken).ConfigureAwait(false);
        return node is null ? NotFound() : Wire(node);
    }

    /// <summary>
    /// Claims the next durable remote job for a worker, or returns no content when none is available.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The leased immutable job snapshot or 204.</returns>
    [HttpPost("jobs/claim")]
    [ProducesResponseType(typeof(JobSnapshot), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status408RequestTimeout)]
    public async Task<IActionResult> Claim(CancellationToken cancellationToken)
    {
        if (Request.Path.Value?.Contains("/Remote/v2/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return StatusCode(StatusCodes.Status409Conflict, "Upgrade to IntroSkipper-Node protocol 3 before claiming new work.");
        }

        var read = await TryReadAsync<ClaimRequest>(cancellationToken).ConfigureAwait(false);
        if (read.TimedOut)
        {
            return StatusCode(StatusCodes.Status408RequestTimeout, "Timed out reading the request body.");
        }

        var request = read.Value;
        if (request is null)
        {
            return BadRequest("Invalid request body.");
        }

        if (string.IsNullOrWhiteSpace(request.WorkerId) || request.WorkerId.Length > 200)
        {
            return BadRequest("A bounded worker id is required.");
        }

        var minutes = Math.Clamp(Plugin.Instance!.Configuration.RemoteAnalysisLeaseMinutes, 1, 120);
        var snapshot = await _database.ClaimRemoteJobAsync(request.WorkerId, TimeSpan.FromMinutes(minutes), cancellationToken, requireNodePolicy: true).ConfigureAwait(false);
        return snapshot is null ? NoContent() : Wire(snapshot);
    }

    /// <summary>
    /// Extends a current lease when its token and generation still match.
    /// </summary>
    /// <param name="jobId">Durable job id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content when renewed; 409 for a stale lease.</returns>
    [HttpPost("jobs/{jobId}/heartbeat")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status408RequestTimeout)]
    public async Task<IActionResult> Heartbeat([FromRoute] Guid jobId, CancellationToken cancellationToken)
    {
        var read = await TryReadAsync<LeaseRequest>(cancellationToken).ConfigureAwait(false);
        if (read.TimedOut)
        {
            return StatusCode(StatusCodes.Status408RequestTimeout, "Timed out reading the request body.");
        }

        var request = read.Value;
        if (request is null || !ValidLease(jobId, request))
        {
            return BadRequest("A job id, lease token, and non-negative lease generation are required.");
        }

        var minutes = Math.Clamp(Plugin.Instance!.Configuration.RemoteAnalysisLeaseMinutes, 1, 120);
        var renewed = await _database.RenewRemoteLeaseAsync(jobId, request.LeaseToken, request.LeaseGeneration, TimeSpan.FromMinutes(minutes), cancellationToken).ConfigureAwait(false);
        return renewed ? NoContent() : Conflict("The lease is stale or expired.");
    }

    /// <summary>
    /// Releases a current lease back to the durable queue.
    /// </summary>
    /// <param name="jobId">Durable job id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content when released; 409 for a stale lease.</returns>
    [HttpPost("jobs/{jobId}/release")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status408RequestTimeout)]
    public async Task<IActionResult> Release([FromRoute] Guid jobId, CancellationToken cancellationToken)
    {
        var read = await TryReadAsync<LeaseRequest>(cancellationToken).ConfigureAwait(false);
        if (read.TimedOut)
        {
            return StatusCode(StatusCodes.Status408RequestTimeout, "Timed out reading the request body.");
        }

        var request = read.Value;
        if (request is null || !ValidLease(jobId, request))
        {
            return BadRequest("A job id, lease token, and non-negative lease generation are required.");
        }

        var released = await _database.ReleaseRemoteLeaseAsync(jobId, request.LeaseToken, request.LeaseGeneration, cancellationToken).ConfigureAwait(false);
        return released ? NoContent() : Conflict("The lease is stale.");
    }

    /// <summary>
    /// Submits one bounded result bundle under a live lease.
    /// </summary>
    /// <param name="jobId">Durable job id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One receipt for every result.</returns>
    [HttpPost("jobs/{jobId}/results")]
    [ProducesResponseType(typeof(List<ImportReceipt>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status408RequestTimeout)]
    public async Task<IActionResult> Submit([FromRoute] Guid jobId, CancellationToken cancellationToken)
    {
        var read = await TryReadAsync<SubmitRequest>(cancellationToken).ConfigureAwait(false);
        if (read.TimedOut)
        {
            return StatusCode(StatusCodes.Status408RequestTimeout, "Timed out reading the request body.");
        }

        var request = read.Value;
        if (request is null || !ValidLease(jobId, new LeaseRequest(request.LeaseToken, request.LeaseGeneration)) || request.Results.Count is 0 or > MaximumItems)
        {
            return BadRequest("A bounded result bundle and current lease are required.");
        }

        try
        {
            var receipts = await _database.SubmitRemoteResultsAsync(jobId, new LeaseRequest(request.LeaseToken, request.LeaseGeneration), request.Results, cancellationToken).ConfigureAwait(false);
            if (_scheduler is not null)
            {
                foreach (var itemId in receipts.Where(receipt => receipt.Outcome == "Accepted" && receipt.Mode is RemoteAnalysisMode.Introduction or RemoteAnalysisMode.Credits).Select(receipt => receipt.ItemId).Distinct())
                {
                    _ = _scheduler.ItemChangedAsync(itemId);
                }
            }

            return Wire(receipts);
        }
        catch (InvalidDataException exception)
        {
            return BadRequest(exception.Message);
        }
    }

    /// <summary>
    /// Promotes a previously held remote proposal after elevated server-side review.
    /// </summary>
    /// <param name="jobId">Durable job id that owns the held proposal.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One receipt for every reviewed result.</returns>
    [HttpPost("jobs/{jobId}/review")]
    [ProducesResponseType(typeof(List<ImportReceipt>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status408RequestTimeout)]
    public async Task<IActionResult> Review([FromRoute] Guid jobId, CancellationToken cancellationToken)
    {
        var read = await TryReadAsync<RemoteReviewRequest>(cancellationToken).ConfigureAwait(false);
        if (read.TimedOut)
        {
            return StatusCode(StatusCodes.Status408RequestTimeout, "Timed out reading the request body.");
        }

        var request = read.Value;
        if (request is null || request.Review is null || request.Results.Count is 0 or > MaximumItems)
        {
            return BadRequest("A bounded held result bundle and review document are required.");
        }

        try
        {
            return Wire(await _database.ReviewRemoteResultsAsync(jobId, request, cancellationToken).ConfigureAwait(false));
        }
        catch (InvalidDataException exception)
        {
            return BadRequest(exception.Message);
        }
    }

    /// <summary>
    /// Gets the current canonical server result for an item.
    /// </summary>
    /// <param name="itemId">Library item id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Accepted canonical results, possibly empty.</returns>
    [HttpGet("results/{itemId}")]
    [ProducesResponseType(typeof(List<CanonicalResult>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetResults([FromRoute] Guid itemId, CancellationToken cancellationToken)
        => Wire(await _database.GetRemoteResultsAsync(itemId, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Gets the current server revision for every requested item's remote modes.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The revision vector.</returns>
    [HttpPost("results/revisions")]
    [ProducesResponseType(typeof(List<RevisionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status408RequestTimeout)]
    public async Task<IActionResult> GetRevisions(CancellationToken cancellationToken)
    {
        var read = await TryReadAsync<RevisionRequest>(cancellationToken).ConfigureAwait(false);
        if (read.TimedOut)
        {
            return StatusCode(StatusCodes.Status408RequestTimeout, "Timed out reading the request body.");
        }

        var request = read.Value;
        if (request is null || request.ItemIds.Count is 0 or > MaximumItems || request.ItemIds.Any(id => id == Guid.Empty) || request.ItemIds.Distinct().Count() != request.ItemIds.Count)
        {
            return BadRequest("One through 100 distinct item ids are required.");
        }

        return Wire(await _database.GetRemoteRevisionResponsesAsync(request.ItemIds, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Exports one pending server job as an explicit offline work package.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The immutable offline snapshot or 404 when the job cannot be exported.</returns>
    [HttpPost("manifests/export")]
    [ProducesResponseType(typeof(JobSnapshot), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status408RequestTimeout)]
    public async Task<IActionResult> ExportManifest(CancellationToken cancellationToken)
    {
        var read = await TryReadAsync<RemoteManifestExportRequest>(cancellationToken).ConfigureAwait(false);
        if (read.TimedOut)
        {
            return StatusCode(StatusCodes.Status408RequestTimeout, "Timed out reading the request body.");
        }

        var request = read.Value;
        if (request is null || request.JobId == Guid.Empty)
        {
            return BadRequest("A pending job id is required.");
        }

        var snapshot = await _database.ExportRemoteManifestAsync(request.JobId, cancellationToken).ConfigureAwait(false);
        return snapshot is null ? NotFound() : Wire(snapshot);
    }

    /// <summary>
    /// Imports results for an explicit offline work package without creating a lease.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One admission receipt for every imported result.</returns>
    [HttpPost("imports")]
    [ProducesResponseType(typeof(List<ImportReceipt>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status408RequestTimeout)]
    public async Task<IActionResult> Import(CancellationToken cancellationToken)
    {
        var read = await TryReadAsync<ImportRequest>(cancellationToken).ConfigureAwait(false);
        if (read.TimedOut)
        {
            return StatusCode(StatusCodes.Status408RequestTimeout, "Timed out reading the request body.");
        }

        var request = read.Value;
        if (request is null || string.IsNullOrWhiteSpace(request.OfflineContextId) || request.Results.Count is 0 or > MaximumItems)
        {
            return BadRequest("A bounded exported context and result bundle are required.");
        }

        try
        {
            return Wire(await _database.ImportRemoteResultsAsync(request, cancellationToken).ConfigureAwait(false));
        }
        catch (InvalidDataException exception)
        {
            return BadRequest(exception.Message);
        }
    }

    private async Task<ReadBodyResult<T>> TryReadAsync<T>(CancellationToken cancellationToken)
        where T : class
    {
        if (Request.ContentLength is > MaximumBodyBytes)
        {
            return new(null, false);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, HttpContext.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var buffer = new byte[81920];
            var remaining = MaximumBodyBytes;
            using var body = new MemoryStream();
            while (true)
            {
                var read = await Request.Body.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)), deadline.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (read > remaining)
                {
                    return new(null, false);
                }

                await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }

            return new(body.Length == 0 ? null : WireJson.Read<T>(Encoding.UTF8.GetString(body.GetBuffer(), 0, checked((int)body.Length))), false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !HttpContext.RequestAborted.IsCancellationRequested)
        {
            return new(null, true);
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException)
        {
            return new(null, false);
        }
    }

    private static bool ValidLease(Guid jobId, LeaseRequest? request)
        => jobId != Guid.Empty && request is { LeaseGeneration: >= 0 } && !string.IsNullOrWhiteSpace(request.LeaseToken) && request.LeaseToken.Length <= 256;

    private List<RemoteLibrary> GetLibraryCatalog()
        => (_libraryManager.GetVirtualFolders() ?? [])
            .Where(folder => Guid.TryParse(folder.ItemId, out _))
            .Select(folder => new RemoteLibrary
            {
                Id = Guid.Parse(folder.ItemId),
                Name = folder.Name,
                CollectionType = folder.CollectionType?.ToString() ?? string.Empty,
                Paths = folder.Locations?.ToList() ?? [],
            })
            .OrderBy(library => library.Name, StringComparer.Ordinal)
            .ThenBy(library => library.Id)
            .ToList();

    private ContentResult Wire<T>(T value)
        => Content(WireJson.Write(value), MediaTypeNames.Application.Json);

    private readonly record struct ReadBodyResult<T>(T? Value, bool TimedOut)
        where T : class;
}
