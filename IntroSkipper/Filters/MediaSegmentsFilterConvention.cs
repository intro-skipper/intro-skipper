// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace IntroSkipper.Filters;

/// <summary>
/// Applies <see cref="MediaSegmentsFirstEpisodeFilter"/> only to MediaSegments actions that accept an itemId.
/// </summary>
internal sealed class MediaSegmentsFilterConvention : IApplicationModelConvention
{
    /// <inheritdoc />
    public void Apply(ApplicationModel application)
    {
        foreach (var controller in application.Controllers)
        {
            if (!controller.ControllerName.Contains("MediaSegments", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var action in controller.Actions)
            {
                if (action.Parameters.Any(p => p.ParameterName.Equals("itemId", StringComparison.OrdinalIgnoreCase)))
                {
                    action.Filters.Add(new ServiceFilterAttribute(typeof(MediaSegmentsFirstEpisodeFilter)));
                }
            }
        }
    }
}
