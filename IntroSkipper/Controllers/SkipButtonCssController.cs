// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Globalization;
using System.Text.RegularExpressions;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Branding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Controllers;

/// <summary>
/// Extended API for SkipButtonCss.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="SkipButtonCssController"/> class.
/// </remarks>
/// <param name="serverConfigurationManager">ServerConfigurationManager.</param>
/// <param name="logger">Logger.</param>
[ApiController]
[Route("SkipButtonCss")]
public partial class SkipButtonCssController(IServerConfigurationManager serverConfigurationManager, ILogger<SkipButtonCssController> logger) : ControllerBase
{
    private const string ImportString = """@import url("https://cdn.jsdelivr.net/gh/intro-skipper/intro-skipper-css@main/skip-button.min.css");""";

    private const string RootCssTemplate = """
:root {
    /* Skip button timing */
    --skip-hide-duration: {{SKIP_HIDE_DELAY}}s;
}
""";

    private readonly IServerConfigurationManager _serverConfigurationManager = serverConfigurationManager;

    private readonly ILogger<SkipButtonCssController> _logger = logger;

    private static string SkipHideDuration =>
        (Plugin.Instance?.Configuration?.SkipbuttonHideDelay ?? 8).ToString(CultureInfo.InvariantCulture);

    private static string RootCss =>
        RootCssTemplate.Replace("{{SKIP_HIDE_DELAY}}", SkipHideDuration, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Injects the skip button CSS into branding configuration.
    /// Adds the @import statement and :root block with --skip-hide-duration variable.
    /// Called when the "Inject CSS" button is pressed.
    /// </summary>
    /// <response code="200">CSS injected successfully.</response>
    /// <returns>Update status.</returns>
    [HttpPost("InjectCss")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult InjectCss()
    {
        var currentBranding = (BrandingOptions)_serverConfigurationManager.GetConfiguration("branding");
        var existingCss = currentBranding.CustomCss ?? string.Empty;
        var cssModified = false;

        // Check if import already inserted, if not add it
        if (!existingCss.Contains(ImportString, StringComparison.OrdinalIgnoreCase))
        {
            existingCss = InjectImport(existingCss);
            cssModified = true;
            LogInjectedCssImport(_logger);
        }

        // Update or inject --skip-hide-duration
        var (updatedCss, durationModified) = UpdateDurationValue(existingCss);
        if (durationModified)
        {
            existingCss = updatedCss;
            cssModified = true;
        }

        if (cssModified)
        {
            currentBranding.CustomCss = existingCss;
            _serverConfigurationManager.SaveConfiguration("branding", currentBranding);
        }
        else
        {
            LogCssAlreadyUpToDate(_logger);
        }

        return Ok();
    }

    /// <summary>
    /// Updates the --skip-hide-duration CSS variable if it already exists in branding CSS.
    /// Called when the config Save button is pressed.
    /// Does nothing if the CSS has not been injected yet.
    /// </summary>
    /// <response code="200">Duration updated or no action needed.</response>
    /// <returns>Update status.</returns>
    [HttpPost("UpdateSkipDuration")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult UpdateSkipDuration()
    {
        var currentBranding = (BrandingOptions)_serverConfigurationManager.GetConfiguration("branding");
        var existingCss = currentBranding.CustomCss ?? string.Empty;

        // Only update if --skip-hide-duration already exists
        if (!SkipDurationRegex().IsMatch(existingCss))
        {
            LogSkipDurationNotFound(_logger);
            return Ok();
        }

        var (updatedCss, modified) = UpdateDurationValue(existingCss);
        if (modified)
        {
            currentBranding.CustomCss = updatedCss;
            _serverConfigurationManager.SaveConfiguration("branding", currentBranding);
        }

        return Ok();
    }

    /// <summary>
    /// Updates or injects the --skip-hide-duration value in the CSS.
    /// </summary>
    /// <returns>Tuple of (updated CSS, whether it was modified).</returns>
    private (string Css, bool Modified) UpdateDurationValue(string css)
    {
        var expectedValue = $"--skip-hide-duration: {SkipHideDuration}s;";
        var regex = SkipDurationRegex();

        if (regex.IsMatch(css))
        {
            var currentMatch = regex.Match(css);
            if (!currentMatch.Value.Equals(expectedValue, StringComparison.OrdinalIgnoreCase))
            {
                LogUpdatedSkipDuration(_logger, SkipHideDuration);
                return (regex.Replace(css, expectedValue), true);
            }

            return (css, false);
        }

        // No existing value, append :root block
        LogInjectedRootBlock(_logger, SkipHideDuration);
        return (css + Environment.NewLine + RootCss, true);
    }

    /// <summary>
    /// Injects the import statement after the last existing @import, or prepends if none exist.
    /// </summary>
    private static string InjectImport(string css)
    {
        var lastImportIndex = css.LastIndexOf("@import", StringComparison.OrdinalIgnoreCase);

        if (lastImportIndex >= 0)
        {
            var semicolonIndex = css.IndexOf(';', lastImportIndex);
            if (semicolonIndex >= 0)
            {
                var insertPosition = semicolonIndex + 1;

                // Skip past newline if present
                if (insertPosition < css.Length && css[insertPosition] == '\n')
                {
                    insertPosition++;
                }
                else if (insertPosition + 1 < css.Length &&
                         css[insertPosition] == '\r' &&
                         css[insertPosition + 1] == '\n')
                {
                    insertPosition += 2;
                }

                return css.Insert(insertPosition, ImportString + Environment.NewLine);
            }

            // Malformed @import, append to end
            return css + Environment.NewLine + ImportString;
        }

        // No existing @import, prepend our import
        return ImportString + Environment.NewLine + css;
    }

    [GeneratedRegex(@"--skip-hide-duration:\s*[\d.]+s;", RegexOptions.IgnoreCase)]
    private static partial Regex SkipDurationRegex();

    [LoggerMessage(Level = LogLevel.Information, Message = "Injected skip button CSS import")]
    private static partial void LogInjectedCssImport(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skip button CSS already up to date, no changes made")]
    private static partial void LogCssAlreadyUpToDate(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "--skip-hide-duration not found in CSS, skipping update")]
    private static partial void LogSkipDurationNotFound(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Updated --skip-hide-duration to {Duration}s")]
    private static partial void LogUpdatedSkipDuration(ILogger logger, string duration);

    [LoggerMessage(Level = LogLevel.Information, Message = "Injected :root block with --skip-hide-duration: {Duration}s")]
    private static partial void LogInjectedRootBlock(ILogger logger, string duration);
}
