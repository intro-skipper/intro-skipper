// SPDX-FileCopyrightText: 2025-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;

namespace IntroSkipper.Tests;

internal static class WindowsFfmpegTestBootstrap
{
    // Keep this in sync with the current Jellyfin ffmpeg requirement.
    private const string FfmpegZipUrl = "https://github.com/jellyfin/jellyfin-ffmpeg/releases/download/v7.1.3-1/jellyfin-ffmpeg_7.1.3-1_portable_win64-clang-gpl.zip";

    [ModuleInitializer]
    internal static void Init()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        EnsureFreshFfmpegOnPath();
    }

    private static void EnsureFreshFfmpegOnPath()
    {
        // Use the test output directory so this works both locally and in CI.
        var baseDir = AppContext.BaseDirectory;

        // Guard against concurrent test processes (e.g., VS Test Explorer + `dotnet test`).
        // The failure we want to avoid is: "ffmpeg.zip is being used by another process".
        using var mutex = new Mutex(initiallyOwned: false, name: @"Global\IntroSkipper.Tests.FFmpegBootstrap");
        var acquired = false;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.FromMinutes(5));
            if (!acquired)
            {
                throw new TimeoutException("Timed out waiting for FFmpeg bootstrap mutex.");
            }

            var rootDir = Path.Combine(baseDir, "_ffmpeg");

            // Requirement: always download the current ffmpeg on Windows for tests.
            // Try to clear any existing download/extract first.
            try
            {
                if (Directory.Exists(rootDir))
                {
                    ExecuteWithRetry(
                        () => Directory.Delete(rootDir, recursive: true),
                        maxAttempts: 6,
                        baseDelayMs: 150);
                }
            }
            catch
            {
                // If cleanup fails (e.g., AV locks an .exe), fall back to a unique folder.
                rootDir = Path.Combine(baseDir, "_ffmpeg", Guid.NewGuid().ToString("N"));
            }

            Directory.CreateDirectory(rootDir);

            var originalZipFileName = TryGetZipFileNameFromUrl(FfmpegZipUrl);
            var zipFileName = string.IsNullOrWhiteSpace(originalZipFileName) ? "ffmpeg.zip" : originalZipFileName;
            var zipPath = Path.Combine(rootDir, zipFileName);
            ExecuteWithRetry(
                () => DownloadFile(FfmpegZipUrl, zipPath),
                maxAttempts: 6,
                baseDelayMs: 150);

            var extractDir = Path.Combine(rootDir, "extract");
            Directory.CreateDirectory(extractDir);
            ExecuteWithRetry(
                () => ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true),
                maxAttempts: 6,
                baseDelayMs: 150);

            // Best-effort cleanup; avoid leaving the zip around for subsequent runs.
            if (!string.Equals(zipFileName, originalZipFileName, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    ExecuteWithRetry(
                        () => File.Delete(zipPath),
                        maxAttempts: 3,
                        baseDelayMs: 150);
                }
                catch
                {
                    // Ignore cleanup errors.
                }
            }

            var ffmpegExe = Directory.EnumerateFiles(extractDir, "ffmpeg.exe", SearchOption.AllDirectories)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(ffmpegExe))
            {
                throw new InvalidOperationException(
                    $"FFmpeg bootstrap failed: 'ffmpeg.exe' not found after extracting '{FfmpegZipUrl}' to '{extractDir}'.");
            }

            var ffmpegDir = Path.GetDirectoryName(ffmpegExe);
            if (string.IsNullOrWhiteSpace(ffmpegDir))
            {
                throw new InvalidOperationException(
                    $"FFmpeg bootstrap failed: could not determine directory for '{ffmpegExe}'.");
            }

            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

            // Ensure our ffmpeg is preferred over any globally installed one.
            Environment.SetEnvironmentVariable("PATH", ffmpegDir + Path.PathSeparator + currentPath, EnvironmentVariableTarget.Process);
        }
        finally
        {
            if (acquired)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch
                {
                    // Ignore release failures.
                }
            }
        }
    }

    private static void ExecuteWithRetry(Action action, int maxAttempts, int baseDelayMs)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransientIo(ex))
            {
                // Simple exponential backoff.
                var delayMs = baseDelayMs * (1 << Math.Min(attempt - 1, 5));
                Thread.Sleep(delayMs);
            }
        }

        // Final attempt (let any exception bubble with original stack).
        action();
    }

    private static bool IsTransientIo(Exception ex)
        => ex is IOException || ex is UnauthorizedAccessException;

    private static string? TryGetZipFileNameFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var fileName = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        return fileName;
    }

    private static void DownloadFile(string url, string destinationFilePath)
    {
        using var httpClient = new HttpClient();
        using var response = httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        using var httpStream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        using var fileStream = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
        httpStream.CopyTo(fileStream);
        fileStream.Flush(flushToDisk: true);
    }
}
