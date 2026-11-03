// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Helper;
using Xunit;

/// <summary>
/// Tests for <see cref="FileHash"/>.
/// </summary>
public sealed class TestFileHash : IDisposable
{
    private readonly TempDirectory _dir = new();

    [Fact]
    public async Task CopiedFile_HashesTheSame()
    {
        var original = Write("a.mkv", RandomNumberGenerator.GetBytes(1_000_000));
        var copy = _dir.Join("renamed.mkv");
        File.Copy(original, copy);

        Assert.Equal(await HashAsync(original), await HashAsync(copy));
    }

    [Theory]
    [InlineData(1_000_000, 1_000_000 - FileHash.SampleLength)]
    [InlineData(1_000_000, 999_999)]
    [InlineData(10, 0)]
    public async Task ByteChangedInTheTail_ChangesTheHash(int length, int offset)
    {
        var bytes = new byte[length];
        var before = await HashAsync(Write("a.mkv", bytes));

        bytes[offset] = 1;

        Assert.NotEqual(before, await HashAsync(Write("a.mkv", bytes)));
    }

    [Fact]
    public async Task ByteChangedBeforeTheTail_KeepsTheHash()
    {
        // An in-place header edit moves no timing, so the file keeps its identity.
        var bytes = new byte[1_000_000];
        var before = await HashAsync(Write("a.mkv", bytes));

        bytes[0] = 1;

        Assert.Equal(before, await HashAsync(Write("a.mkv", bytes)));
    }

    [Fact]
    public async Task FilesThatDifferOnlyInLength_HashDifferently()
    {
        // Both tails are zeros, so only the length tells the files apart.
        var length = FileHash.SampleLength + 1;
        Assert.NotEqual(await HashAsync(Write("a.mkv", new byte[length])), await HashAsync(Write("b.mkv", new byte[length + 1])));
    }

    public void Dispose() => _dir.Dispose();

    private static Task<string> HashAsync(string path) => FileHash.ComputeAsync(path, CancellationToken.None);

    private string Write(string name, byte[] bytes)
    {
        var path = _dir.Join(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
