// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace IntroSkipper.Helper;

/// <summary>
/// Computes an item's file hash: SHA-256 over the file's length and its last 64 KiB.
/// Equal hashes mean the same file, so stored times apply to both. One read that ends at
/// the file's end costs one seek, and on a cloud mount it cannot fetch past what it reads,
/// as a read-ahead after a read inside the file does.
/// </summary>
public static class FileHash
{
    /// <summary>
    /// The length of the hashed tail, in bytes.
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

        // A file shorter than the sample is hashed whole.
        var buffer = new byte[Math.Min(length, SampleLength)];
        var offset = length - buffer.Length;
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

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var lengthBytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(lengthBytes, length);
        sha.AppendData(lengthBytes);
        sha.AppendData(buffer);
        return Convert.ToHexString(sha.GetHashAndReset());
    }
}
