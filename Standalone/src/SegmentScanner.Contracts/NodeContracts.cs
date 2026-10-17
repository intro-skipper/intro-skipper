using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SegmentScanner.Contracts;

/// <summary>
/// A server-visible media-path prefix available to a remote scanner node.
/// </summary>
public sealed record RemoteNodePath
{
    public required string Prefix { get; init; }

    public bool? CaseSensitive { get; init; }

    /// <summary>
    /// Determines whether a server media path is within this declared path prefix.
    /// </summary>
    /// <param name="sourcePath">The server media path to evaluate.</param>
    /// <returns>Whether the path equals the prefix or is a descendant of it.</returns>
    public bool Matches(string sourcePath)
        => NodeScope.MatchesPath(Prefix, CaseSensitive, sourcePath);

}

/// <summary>
/// The standalone-owned declaration of a scanner node's server scope.
/// </summary>
public record RemoteNodeRegistration
{
    public required string NodeId { get; init; }

    public required string Name { get; init; }

    public List<Guid> LibraryIds { get; init; } = [];

    public List<RemoteAnalysisMode> Modes { get; init; } = [];

    public List<RemoteNodePath> ServerPaths { get; init; } = [];

    public string CreditsProfileHash { get; init; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public CreditsDetectionMethod CreditsMethod { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AutomaticImports { get; init; }

    public int LeaseMinutes { get; init; } = 30;
}

/// <summary>
/// The current persisted server view of a scanner node declaration.
/// </summary>
public sealed record RemoteNodeInfo : RemoteNodeRegistration
{
    public RemoteNodePolicy? Policy { get; init; }

    public NodeRuntimeInformation? Runtime { get; init; }

    public bool IsLocal { get; init; }

    public DateTimeOffset? LastSeenUtc { get; init; }

    public bool Enabled { get; init; }

    public long Revision { get; init; }

    public DateTimeOffset UpdatedUtc { get; init; }
}

/// <summary>
/// Changes only the server-controlled availability of a registered node.
/// </summary>
/// <param name="Enabled">Whether the node may claim and publish new work.</param>
public sealed record NodeEnabledRequest(bool Enabled);

/// <summary>
/// A selectable Jellyfin media library exposed by the server bridge.
/// </summary>
public sealed record RemoteLibrary
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public string CollectionType { get; init; } = string.Empty;

    public List<string> Paths { get; init; } = [];
}

/// <summary>
/// The standalone settings that declare the local scanner's server scope.
/// </summary>
public sealed record NodeSelectionConfiguration
{
    public bool Configured { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AutomaticImports { get; init; }

    public string Name { get; init; } = Environment.MachineName;

    public List<Guid> LibraryIds { get; init; } = [];

    public List<RemoteAnalysisMode> Modes { get; init; } = [RemoteAnalysisMode.Introduction, RemoteAnalysisMode.Credits];

    public int LeaseMinutes { get; init; } = 30;
}

/// <summary>
/// Shared path and declaration validation for server-owned remote-node scope.
/// </summary>
public static partial class NodeScope
{
    private static readonly Regex WindowsDrivePath = WindowsDrivePathRegex();

    /// <summary>
    /// Determines whether a path is contained by a declared server prefix.
    /// </summary>
    /// <param name="prefix">The declared absolute server prefix.</param>
    /// <param name="caseSensitive">An optional explicit comparison mode.</param>
    /// <param name="path">The server media path to evaluate.</param>
    /// <returns>Whether the path equals the prefix or is a descendant of it.</returns>
    public static bool MatchesPath(string prefix, bool? caseSensitive, string path)
    {
        var normalizedPrefix = NormalizeAbsolutePath(prefix);
        var normalizedPath = NormalizeAbsolutePath(path);
        var comparison = caseSensitive ?? !IsWindowsPath(normalizedPrefix)
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        return string.Equals(normalizedPrefix, normalizedPath, comparison)
            || normalizedPath.StartsWith(normalizedPrefix.EndsWith('/') ? normalizedPrefix : normalizedPrefix + "/", comparison);
    }

    /// <summary>
    /// Validates a standalone declaration before it reaches server persistence.
    /// </summary>
    /// <param name="registration">The incoming declaration.</param>
    public static void ValidateRegistration(RemoteNodeRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ContractValidation.Require(IsBoundedText(registration.NodeId, 200), "A bounded node id is required.");
        ContractValidation.Require(IsBoundedText(registration.Name, 200), "A bounded node name is required.");
        ContractValidation.Require(Enum.IsDefined(registration.CreditsMethod), "Unsupported credits detection method.");
        ContractValidation.Require(registration.LibraryIds.Count <= 100 && registration.LibraryIds.All(id => id != Guid.Empty) && registration.LibraryIds.Distinct().Count() == registration.LibraryIds.Count, "A node needs distinct library ids.");
        ContractValidation.Require(registration.Modes.Count <= 5 && registration.Modes.All(IsSupportedMode) && registration.Modes.Distinct().Count() == registration.Modes.Count, "A node needs distinct supported modes.");
        var hasScope = registration.LibraryIds.Count > 0 && registration.Modes.Count > 0;
        ContractValidation.Require(registration.ServerPaths.Count <= 32 && (!hasScope || registration.ServerPaths.Count > 0), "Scoped nodes need bounded server paths.");
        foreach (var path in registration.ServerPaths)
        {
            ContractValidation.Require(path is not null && IsValidPrefix(path.Prefix), "Node paths must be absolute and cannot traverse parents.");
        }

        ContractValidation.Require(registration.ServerPaths.Select(path => NormalizeAbsolutePath(path.Prefix)).Distinct(StringComparer.OrdinalIgnoreCase).Count() == registration.ServerPaths.Count, "Duplicate node paths are not allowed.");
        ContractValidation.Require(registration.LeaseMinutes is >= 1 and <= 120, "Node lease minutes must be between 1 and 120.");
        ContractValidation.Require(!registration.Modes.Contains(RemoteAnalysisMode.Credits) || ContractValidation.IsSha256(registration.CreditsProfileHash), "Credits nodes require a lowercase SHA-256 profile hash.");
    }

    /// <summary>
    /// Validates a persisted local selection before registration.
    /// </summary>
    /// <param name="selection">The local settings selection.</param>
    public static void ValidateSelection(NodeSelectionConfiguration selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ContractValidation.Require(IsBoundedText(selection.Name, 200), "A bounded node name is required.");
        ContractValidation.Require(selection.LibraryIds.Count is <= 100 && selection.LibraryIds.All(id => id != Guid.Empty) && selection.LibraryIds.Distinct().Count() == selection.LibraryIds.Count, "Invalid node library ids.");
        ContractValidation.Require(selection.Modes.Count <= 5 && selection.Modes.All(IsSupportedMode) && selection.Modes.Distinct().Count() == selection.Modes.Count, "Invalid node modes.");
        ContractValidation.Require(selection.LeaseMinutes is >= 1 and <= 120, "Node lease minutes must be between 1 and 120.");
    }

    /// <summary>
    /// Normalizes an absolute server path for comparison and persistence.
    /// </summary>
    /// <param name="path">The input path.</param>
    /// <returns>The slash-normalized absolute path.</returns>
    public static string NormalizeAbsolutePath(string path)
    {
        ContractValidation.Require(IsValidPrefix(path), "Paths must be absolute and cannot traverse parents.");
        var normalized = path.Replace('\\', '/');
        while (normalized.Contains("//", StringComparison.Ordinal) && !normalized.StartsWith("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        return normalized.Length > 1 && !WindowsDrivePath.IsMatch(normalized) ? normalized.TrimEnd('/') : normalized;
    }

    private static bool IsValidPrefix(string? path)
    {
        if (!IsBoundedText(path, 4096))
        {
            return false;
        }

        var normalized = path!.Replace('\\', '/');
        if (!(normalized.StartsWith('/') || normalized.StartsWith("//", StringComparison.Ordinal) || WindowsDrivePath.IsMatch(normalized)))
        {
            return false;
        }

        return !normalized.Split('/').Any(part => part is "." or "..");
    }

    private static bool IsWindowsPath(string path)
        => WindowsDrivePath.IsMatch(path) || path.StartsWith("//", StringComparison.Ordinal);

    private static bool IsBoundedText(string? value, int maximumLength)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && !value.Contains('\0', StringComparison.Ordinal);

    private static bool IsSupportedMode(RemoteAnalysisMode mode)
        => Enum.IsDefined(mode);

    [GeneratedRegex("^[A-Za-z]:/(?:$|.*)", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsDrivePathRegex();
}
