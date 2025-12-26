using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;

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
        var rootDir = Path.Combine(baseDir, "_ffmpeg");

        // Requirement: always download the current ffmpeg on Windows for tests.
        // Try to clear any existing download/extract first.
        try
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
        catch
        {
            // If cleanup fails (e.g., AV locks an .exe), fall back to a unique folder.
            rootDir = Path.Combine(baseDir, "_ffmpeg", Guid.NewGuid().ToString("N"));
        }

        Directory.CreateDirectory(rootDir);

        var zipPath = Path.Combine(rootDir, "ffmpeg.zip");
        DownloadFile(FfmpegZipUrl, zipPath);

        var extractDir = Path.Combine(rootDir, "extract");
        Directory.CreateDirectory(extractDir);
        ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

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

    private static void DownloadFile(string url, string destinationFilePath)
    {
        using var httpClient = new HttpClient();
        using var response = httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        using var httpStream = response.Content.ReadAsStream();
        using var fileStream = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
        httpStream.CopyTo(fileStream);
    }
}
