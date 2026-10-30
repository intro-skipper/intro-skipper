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
    [InlineData(0)]
    [InlineData(500_000)]
    [InlineData(999_999)]
    public async Task ByteChangedInASample_ChangesTheHash(int offset)
    {
        var bytes = new byte[1_000_000];
        var before = await HashAsync(Write("a.mkv", bytes));

        bytes[offset] = 1;

        Assert.NotEqual(before, await HashAsync(Write("a.mkv", bytes)));
    }

    [Fact]
    public async Task FilesThatDifferOnlyInLength_HashDifferently()
    {
        // Every sample of both files is zeros, so only the length tells them apart.
        var length = (3 * FileHash.SampleLength) + 1;
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
