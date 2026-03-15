// SPDX-FileCopyrightText: 2022-2023 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2025 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Globalization;
using System.IO;
using System.Net.Mime;
using System.Text;
using IntroSkipper.Data;
using IntroSkipper.Helper;
using MediaBrowser.Common;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Controllers;

/// <summary>
/// Troubleshooting controller.
/// </summary>
[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
[Route("IntroSkipper")]
public partial class TroubleshootingController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationHost _applicationHost;
    private readonly ILogger<TroubleshootingController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TroubleshootingController"/> class.
    /// </summary>
    /// <param name="applicationHost">Application host.</param>
    /// <param name="libraryManager">Library Manager.</param>
    /// <param name="logger">Logger.</param>
    public TroubleshootingController(
        IApplicationHost applicationHost,
        ILibraryManager libraryManager,
        ILogger<TroubleshootingController> logger)
    {
        _libraryManager = libraryManager;
        _applicationHost = applicationHost;
        _logger = logger;
    }

    /// <summary>
    /// Plugin meta endpoint.
    /// </summary>
    /// <returns>The version info.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public JsonResult GetPluginMetadata()
    {
        var json = new
        {
            version = Plugin.Instance!.Version.ToString(3),
        };

        return new JsonResult(json);
    }

    /// <summary>
    /// Gets a Markdown formatted support bundle.
    /// </summary>
    /// <response code="200">Support bundle created.</response>
    /// <returns>Support bundle.</returns>
    [HttpGet("SupportBundle")]
    [Produces(MediaTypeNames.Text.Plain)]
    public ActionResult<string> GetSupportBundle()
    {
        ArgumentNullException.ThrowIfNull(Plugin.Instance);

        var bundle = new StringBuilder();

        bundle.Append("* Jellyfin version: ");
        bundle.Append(_applicationHost.ApplicationVersionString);
        bundle.Append('\n');

        var version = Plugin.Instance.Version.ToString(3);

        try
        {
            var commit = Commit.CommitHash;
            if (!string.IsNullOrWhiteSpace(commit))
            {
                version += string.Concat("+", commit.AsSpan(0, 12));
            }
        }
        catch (Exception ex)
        {
            LogUnableToAppendCommit(_logger, ex);
        }

        bundle.Append("* Plugin version: ");
        bundle.Append(version);
        bundle.Append('\n');

        bundle.Append("* Runs on: ");
        bundle.Append(Helper.OperatingSystem.DetermineOperatingSystem());
        bundle.Append('\n');

        bundle.Append("* Queue contents: ");
        bundle.Append(Plugin.Instance.TotalQueued);
        bundle.Append(" episodes, ");
        bundle.Append(Plugin.Instance.TotalSeasons);
        bundle.Append(" seasons\n");

        bundle.Append("* Warnings: `");
        bundle.Append(WarningManager.GetWarnings());
        bundle.Append("`\n");

        bundle.Append(FFmpegWrapper.GetChromaprintLogs());

        return bundle.ToString();
    }

    /// <summary>
    /// Gets a Markdown formatted support bundle.
    /// </summary>
    /// <response code="200">Support bundle created.</response>
    /// <returns>Support bundle.</returns>
    [HttpGet("Storage")]
    [Produces(MediaTypeNames.Text.Plain)]
    public ActionResult<string> GetFreeSpace()
    {
        ArgumentNullException.ThrowIfNull(Plugin.Instance);
        var bundle = new StringBuilder();

        var libraries = _libraryManager.GetVirtualFolders();
        foreach (var library in libraries)
        {
            bundle.AppendFormat(CultureInfo.CurrentCulture, "Library: {0}\n", library.Name);

            if (library.Locations.Length == 0)
            {
                bundle.Append("No locations found for this library.\n\n");
                continue;
            }

            foreach (var location in library.Locations)
            {
                try
                {
                    DriveInfo driveInfo = new DriveInfo(location);
                    // Get available free space in bytes
                    long availableFreeSpace = driveInfo.AvailableFreeSpace;

                    // Get total size of the drive in bytes
                    long totalSize = driveInfo.TotalSize;

                    // Get total used space in Percentage
                    double usedSpacePercentage = totalSize > 0 ? (totalSize - availableFreeSpace) / (double)totalSize * 100 : 0;

                    bundle.AppendFormat(CultureInfo.CurrentCulture, "Location: {0}\n", location);
                    bundle.AppendFormat(CultureInfo.CurrentCulture, "Drive: {0}\n", driveInfo.Name);
                    bundle.AppendFormat(CultureInfo.CurrentCulture, "Total Size: {0}\n", GetHumanReadableSize(totalSize));
                    bundle.AppendFormat(CultureInfo.CurrentCulture, "Available Free Space: {0}\n", GetHumanReadableSize(availableFreeSpace));
                    bundle.AppendFormat(CultureInfo.CurrentCulture, "Total used in Percentage: {0}%\n", Math.Round(usedSpacePercentage, 2));
                    bundle.Append("-----\n");
                }
                catch (Exception ex)
                {
                    bundle.AppendFormat(CultureInfo.CurrentCulture, "Location: {0}\n", location);
                    bundle.AppendFormat(CultureInfo.CurrentCulture, "Unable to get drive information: {0}\n", ex.Message);
                    bundle.Append("-----\n");
                    LogUnableToGetDriveInfo(_logger, location, ex);
                }
            }

            bundle.Append('\n');
        }

        return bundle.ToString().TrimEnd('\n');
    }

    private static string GetHumanReadableSize(long bytes)
    {
        string[] sizes = ["Bytes", "KB", "MB", "GB", "TB", "PB", "EB", "ZB", "YB"];
        double len = bytes;
        int order = 0;

        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }

        return $"{len:0.##} {sizes[order]}";
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unable to append commit to version: {Exception}")]
    private static partial void LogUnableToAppendCommit(ILogger logger, object exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unable to get DriveInfo for location {Location}: {Exception}")]
    private static partial void LogUnableToGetDriveInfo(ILogger logger, string location, object exception);
}
