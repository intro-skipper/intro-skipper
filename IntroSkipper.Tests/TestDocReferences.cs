// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

/// <summary>
/// Keeps the docs agents navigate by pointing at things that exist. In each doc, every
/// backticked PascalCase identifier must appear in the source, every backticked repo path
/// must resolve, and every ADR reference must name a file in <c>docs/adr</c>. A rename or
/// deletion that leaves a doc behind fails here instead of misleading the next reader.
/// The repo is what git lists, so ignored and excluded folders never count as part of it.
/// </summary>
public sealed partial class TestDocReferences
{
    private static readonly string Root = FindRoot();

    // Build output: a doc may name a path inside it, which exists only after a build.
    private static readonly string[] BuildOutput = ["bin", "obj"];

    private static readonly string[] PathExtensions = [".cs", ".csproj", ".sln", ".md", ".json", ".ts", ".yml", ".props", ".targets", ".html", ".css", ".js"];

    private static readonly Lazy<RepoIndex> Index = new(BuildIndex);

    // The docs agents navigate by. The other files in docs/ are how-tos about the Jellyfin
    // server and name its files, which this repo does not hold.
    public static TheoryData<string> Docs()
    {
        var docs = new TheoryData<string>();
        string[] candidates = ["AGENTS.md", "CONTEXT.md", "docs/segments.md", "docs/analysis.md", "docs/credits.md", "tools/CreditsRunner/README.md"];
        foreach (var doc in candidates.Where(doc => File.Exists(Path.Combine(Root, doc))))
        {
            docs.Add(doc);
        }

        return docs;
    }

    [Theory]
    [MemberData(nameof(Docs))]
    public void EveryReferenceResolves(string doc)
    {
        var text = File.ReadAllText(Path.Combine(Root, doc));
        var index = Index.Value;

        var missing = CodeSpan().Matches(text)
            .Select(match => match.Groups[1].Value)
            .Where(span => !Resolves(span, index))
            .Concat(AdrReference().Matches(text).Select(match => match.Value).Where(adr => !index.Adrs.Contains(adr)))
            .Distinct()
            .ToList();

        Assert.Empty(missing);
    }

    private static bool Resolves(string span, RepoIndex index)
    {
        if (Identifier().IsMatch(span))
        {
            return span.Split('.').All(index.Words.Contains);
        }

        if (!IsRepoPath(span))
        {
            return true;
        }

        var path = span.TrimEnd('/');
        return index.Paths.Contains(path)
            || index.Paths.Contains("IntroSkipper/" + path)
            || (!path.Contains('/', StringComparison.Ordinal) && index.FileNames.Contains(path));
    }

    // A path is a span naming a folder (trailing slash) or a file with a source or doc
    // extension. Routes, globs, parent-relative paths and build output are not paths here.
    private static bool IsRepoPath(string span)
        => !span.Any(char.IsWhiteSpace)
            && !span.StartsWith("../", StringComparison.Ordinal)
            && !span.Contains('{', StringComparison.Ordinal)
            && !span.Contains('*', StringComparison.Ordinal)
            && !span.Split('/').Intersect(BuildOutput).Any()
            && (span.EndsWith('/') || PathExtensions.Any(extension => span.EndsWith(extension, StringComparison.Ordinal)));

    private static RepoIndex BuildIndex()
    {
        var files = ListRepoFiles();
        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files.Where(file => file.EndsWith(".cs", StringComparison.Ordinal)
            || file.EndsWith(".ts", StringComparison.Ordinal)
            || file.EndsWith(".csproj", StringComparison.Ordinal)))
        {
            words.UnionWith(Word().Matches(File.ReadAllText(Path.Combine(Root, file))).Select(match => match.Value));
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            paths.Add(file);
            for (var slash = file.LastIndexOf('/'); slash > 0; slash = file.LastIndexOf('/', slash - 1))
            {
                paths.Add(file[..slash]);
            }
        }

        var adrs = files
            .Where(file => file.StartsWith("docs/adr/", StringComparison.Ordinal))
            .Select(file => "ADR-" + Path.GetFileName(file)[..4])
            .ToHashSet(StringComparer.Ordinal);

        return new RepoIndex(words, paths, files.Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.Ordinal), adrs);
    }

    // Tracked files plus untracked ones git does not ignore, so a doc can name a file before
    // it is staged. Files deleted on disk but still in the index are left out.
    private static List<string> ListRepoFiles()
    {
        var start = new ProcessStartInfo("git", "ls-files -z --cached --others --exclude-standard")
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var git = Process.Start(start) ?? throw new InvalidOperationException("git did not start");
        var output = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        Assert.Equal(0, git.ExitCode);

        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(file => File.Exists(Path.Combine(Root, file)))
            .ToList();
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "IntroSkipper.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("No IntroSkipper.sln above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"`([^`\r\n]+)`")]
    private static partial Regex CodeSpan();

    [GeneratedRegex(@"^[A-Z][A-Za-z0-9_]*(?:\.[A-Z][A-Za-z0-9_]*)*$")]
    private static partial Regex Identifier();

    [GeneratedRegex(@"\bADR-\d{4}\b")]
    private static partial Regex AdrReference();

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex Word();

    private sealed record RepoIndex(HashSet<string> Words, HashSet<string> Paths, HashSet<string> FileNames, HashSet<string> Adrs);
}
