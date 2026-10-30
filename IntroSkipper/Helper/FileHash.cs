// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace IntroSkipper.Helper;

/// <summary>
/// Computes an item's file hash: SHA-256 over the file's length and three samples from
/// its head, middle and tail. Equal hashes mean the same file, so stored times apply to
/// both; reading three samples instead of the whole file keeps a library backfill to a
/// few seeks per file.
/// </summary>
public static class FileHash
{
    /// <summary>
    /// The length of each sample, in bytes.
    /// </summary>
    internal const int SampleLength = 64 * 1024;

    /// <summary>
    /// Computes the file hash of the file at <paramref name="path"/>.
    /// </summary>
    /// <param name="path">The media file path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task whose result is the hash as upper-case hex.</returns>
    /// <exception cref="IOException">The file cannot be opened or read.</exception>
    /// <exception cref="UnauthorizedAccessException">The server may not read the file.</exception>
    public static async Task<string> ComputeAsync(string path, CancellationToken cancellationToken)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess | FileOptions.Asynchronous);
        var length = RandomAccess.GetLength(handle);

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var lengthBytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(lengthBytes, length);
        sha.AppendData(lengthBytes);

        // A file no longer than three samples is hashed whole.
        var whole = length <= 3L * SampleLength;
        long[] offsets = whole ? [0] : [0, (length / 2) - (SampleLength / 2), length - SampleLength];
        var buffer = new byte[whole ? (int)length : SampleLength];
        foreach (var offset in offsets)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await RandomAccess.ReadAsync(handle, buffer.AsMemory(read), offset + read, cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    throw new IOException($"{path} ended before its reported length of {length} bytes.");
                }

                read += n;
            }

            sha.AppendData(buffer);
        }

        return Convert.ToHexString(sha.GetHashAndReset());
    }
}
