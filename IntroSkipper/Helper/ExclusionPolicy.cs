// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Configuration;
using IntroSkipper.Data;

namespace IntroSkipper.Helper;

internal sealed class ExclusionPolicy
{
    private readonly HashSet<string> _seriesNames;
    private readonly HashSet<string> _movieNames;
    private readonly IReadOnlyList<string> _pathRoots;
    private readonly int _broadPathRootCount;

    private ExclusionPolicy(
        HashSet<string> seriesNames,
        HashSet<string> movieNames,
        IReadOnlyList<string> pathRoots)
    {
        _seriesNames = seriesNames;
        _movieNames = movieNames;
        _pathRoots = pathRoots;
        _broadPathRootCount = CountBroadPathRoots(pathRoots);
    }

    public static ExclusionPolicy Empty { get; } = new([], [], []);

    public int BroadPathRootCount => _broadPathRootCount;

    public static ExclusionPolicy FromConfiguration(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new ExclusionPolicy(
            CreateNameSet(config.SeriesExclusions),
            CreateNameSet(config.MovieExclusions),
            CreatePathRoots(config.PathExclusions));
    }

    public ExclusionDecision EvaluateSeries(string? seriesName, Guid seriesId, string? path)
    {
        _ = seriesId;

        var pathDecision = EvaluatePath(path);
        if (pathDecision.IsExcluded)
        {
            return pathDecision;
        }

        var name = seriesName?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return ExclusionDecision.Included;
        }

        return _seriesNames.Contains(name)
            ? new ExclusionDecision(true, ExclusionReason.SeriesName, name)
            : ExclusionDecision.Included;
    }

    public ExclusionDecision EvaluateMovie(string? movieName, Guid movieId, string? path)
    {
        _ = movieId;

        var pathDecision = EvaluatePath(path);
        if (pathDecision.IsExcluded)
        {
            return pathDecision;
        }

        var name = movieName?.Trim() ?? string.Empty;
        return name.Length > 0 && _movieNames.Contains(name)
            ? new ExclusionDecision(true, ExclusionReason.MovieName, name)
            : ExclusionDecision.Included;
    }

    public bool IsPathExcluded(string? path)
    {
        var normalizedPath = NormalizePath(path);
        if (normalizedPath.Length == 0 || _pathRoots.Count == 0)
        {
            return false;
        }

        foreach (var root in _pathRoots)
        {
            if (IsPathMatch(normalizedPath, root))
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<string> CreateNameSet(IEnumerable<string> entries)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var trimmed = entry.Trim();
            if (trimmed.Length > 0)
            {
                names.Add(trimmed);
            }
        }

        return names;
    }

    private static IReadOnlyList<string> CreatePathRoots(IEnumerable<string> entries)
    {
        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var normalized = NormalizePath(entry);
            if (normalized.Length > 0 && seen.Add(normalized))
            {
                roots.Add(normalized);
            }
        }

        return roots;
    }

    private static int CountBroadPathRoots(IEnumerable<string> roots)
    {
        var count = 0;
        foreach (var root in roots)
        {
            if (IsBroadPathRoot(root))
            {
                count++;
            }
        }

        return count;
    }

    private ExclusionDecision EvaluatePath(string? path)
        => IsPathExcluded(path)
            ? new ExclusionDecision(true, ExclusionReason.Path, "PathExclusions")
            : ExclusionDecision.Included;

    private static bool IsPathMatch(string normalizedPath, string root)
    {
        if (string.Equals(normalizedPath, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (root == "/")
        {
            return normalizedPath.StartsWith('/');
        }

        return normalizedPath.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var normalized = path.Trim().Replace('\\', '/');
        while (normalized.Length > 1 &&
               normalized.EndsWith('/') &&
               !IsDriveRootWithSeparator(normalized))
        {
            normalized = normalized[..^1];
        }

        return IsDriveRootWithSeparator(normalized) ? normalized[..2] : normalized;
    }

    private static bool IsDriveRootWithSeparator(string path)
        => path.Length == 3 &&
           char.IsAsciiLetter(path[0]) &&
           path[1] == ':' &&
           path[2] == '/';

    private static bool IsBroadPathRoot(string path)
    {
        if (path == "/" || IsDriveRoot(path))
        {
            return true;
        }

        return path.StartsWith("//", StringComparison.Ordinal) &&
               path.Split('/', StringSplitOptions.RemoveEmptyEntries).Length == 2;
    }

    private static bool IsDriveRoot(string path)
        => path.Length == 2 &&
           char.IsAsciiLetter(path[0]) &&
           path[1] == ':';
}
