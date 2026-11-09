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
    private const string FfmpegZipUrl = "https://github.com/jellyfin/jellyfin-ffmpeg/releases/download/v8.1.3-1/jellyfin-ffmpeg_8.1.3-1_portable_win64-clang-gpl.zip";

    [ModuleInitializer]
    internal static void Init()
    {
        if (OperatingSystem.IsWindows())
        {
            PutFfmpegOnPath();
        }
    }

    // Each release is downloaded once into the user's local app data and reused, so test runs need
    // no network and a new URL fetches its own release.
    private static void PutFfmpegOnPath()
    {
        // Guards against concurrent test processes (e.g., VS Test Explorer + `dotnet test`).
        using var mutex = new Mutex(initiallyOwned: false, name: @"Global\IntroSkipper.Tests.FFmpegBootstrap");
        if (!mutex.WaitOne(TimeSpan.FromMinutes(5)))
        {
            throw new TimeoutException("Timed out waiting for FFmpeg bootstrap mutex.");
        }

        try
        {
            var release = Path.GetFileNameWithoutExtension(new Uri(FfmpegZipUrl).LocalPath);
            var ffmpegDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IntroSkipper.Tests", "ffmpeg", release);
            if (!File.Exists(Path.Combine(ffmpegDir, "ffmpeg.exe")) || !File.Exists(Path.Combine(ffmpegDir, "ffprobe.exe")))
            {
                Download(ffmpegDir);
            }

            // Ensure our ffmpeg is preferred over any globally installed one.
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            Environment.SetEnvironmentVariable("PATH", ffmpegDir + Path.PathSeparator + currentPath, EnvironmentVariableTarget.Process);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    // Extracts beside ffmpegDir and moves the folder holding ffmpeg.exe into place last, so an
    // interrupted download never leaves a half-filled ffmpegDir that a later run would trust.
    private static void Download(string ffmpegDir)
    {
        var staging = ffmpegDir + ".download";
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        Directory.CreateDirectory(staging);
        var zipPath = Path.Combine(staging, "ffmpeg.zip");

        // Module initializers cannot be async. Use synchronous HTTP APIs here instead of
        // blocking on asynchronous APIs during assembly load.
        using (var httpClient = new HttpClient())
        using (var request = new HttpRequestMessage(HttpMethod.Get, FfmpegZipUrl))
        using (var response = httpClient.Send(request, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            using var httpStream = response.Content.ReadAsStream();
            using var fileStream = File.Create(zipPath);
            httpStream.CopyTo(fileStream);
        }

        var extractDir = Path.Combine(staging, "extract");
        ZipFile.ExtractToDirectory(zipPath, extractDir);
        var ffmpegExe = Directory.EnumerateFiles(extractDir, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new InvalidOperationException($"FFmpeg bootstrap failed: 'ffmpeg.exe' not found after extracting '{FfmpegZipUrl}'.");

        // A folder missing either program is what brought us here, and Move needs the name free.
        if (Directory.Exists(ffmpegDir))
        {
            Directory.Delete(ffmpegDir, recursive: true);
        }

        Directory.Move(Path.GetDirectoryName(ffmpegExe)!, ffmpegDir);
        Directory.Delete(staging, recursive: true);
    }
}
