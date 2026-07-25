// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace IntroSkipper.Filters;

/// <summary>
/// Applies <see cref="MediaSegmentsFirstEpisodeFilter"/> to the read actions of Jellyfin's own
/// media segments controller, and to nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The application model holds every controller the server loaded: Jellyfin clears the default
/// application parts, adds its own API assembly, then adds one part per plugin assembly. Scoping
/// the match to that API assembly is the load-bearing invariant here, because it keeps every
/// plugin controller out of the filter regardless of how it is named or routed. That covers this
/// plugin's own <see cref="Controllers.SegmentEditorController"/>, which exists to serve an
/// unfiltered cross-provider view, and any third-party plugin serving the same route shapes.
/// Attaching to the wrong controller silently rewrites someone else's response body, which
/// nothing at runtime would report, so it is made impossible rather than unlikely.
/// </para>
/// <para>
/// Route templates cannot be used to identify the target. Jellyfin's controllers inherit
/// <c>[Route("[controller]")]</c> and the token is only expanded after conventions run, so at
/// this point their template is the literal <c>[controller]</c> while this plugin's editor route
/// is the literal <c>MediaSegmentsApi</c>. The controller type is read rather than
/// <see cref="ControllerModel.ControllerName"/> because that property is settable and any
/// convention registered earlier can rewrite it.
/// </para>
/// <para>
/// The single intended target is <c>Jellyfin.Api.Controllers.MediaSegmentsController</c>. The
/// match is exact, so it fails open: if that type is renamed or moved upstream, nothing is
/// filtered and season premieres show their intros again, with no error to indicate why.
/// </para>
/// </remarks>
internal sealed class MediaSegmentsFilterConvention : IApplicationModelConvention
{
    private const string CoreApiAssemblyName = "Jellyfin.Api";
    private const string CoreControllerTypeName = "MediaSegmentsController";

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="application"/> is <see langword="null"/>.</exception>
    public void Apply(ApplicationModel application)
    {
        ArgumentNullException.ThrowIfNull(application);

        foreach (var controller in application.Controllers)
        {
            // Type name first: it rejects every other controller with one ordinal compare, so
            // the assembly name is only resolved for the one candidate.
            if (!IsCoreMediaSegmentsController(
                    controller.ControllerType.Name,
                    controller.ControllerType.Assembly.GetName().Name))
            {
                continue;
            }

            foreach (var action in controller.Actions)
            {
                if (ShouldFilterAction(action))
                {
                    action.Filters.Add(new ServiceFilterAttribute(typeof(MediaSegmentsFirstEpisodeFilter)));
                }
            }
        }
    }

    /// <summary>
    /// Determines whether a controller is Jellyfin's own media segments controller.
    /// </summary>
    /// <param name="controllerTypeName">The controller's CLR type name.</param>
    /// <param name="assemblyName">The simple name of the assembly declaring the controller.</param>
    /// <returns><see langword="true"/> if the controller is the core media segments controller; otherwise, <see langword="false"/>.</returns>
    internal static bool IsCoreMediaSegmentsController(string? controllerTypeName, string? assemblyName)
        => string.Equals(controllerTypeName, CoreControllerTypeName, StringComparison.Ordinal)
            && string.Equals(assemblyName, CoreApiAssemblyName, StringComparison.Ordinal);

    /// <summary>
    /// Determines whether an action of the core controller can be served by the filter.
    /// </summary>
    /// <param name="action">The action to test.</param>
    /// <returns><see langword="true"/> if the filter should be attached to the action; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="action"/> is <see langword="null"/>.</exception>
    internal static bool ShouldFilterAction(ActionModel action)
    {
        ArgumentNullException.ThrowIfNull(action);

        // Capability precondition: MediaSegmentsFirstEpisodeFilter resolves the item from an
        // itemId route value or query value only. Attached anywhere else it is a guaranteed
        // no-op plus a debug log on every request.
        if (!action.Parameters.Any(p => p.ParameterName.Equals("itemId", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // Safety precondition: the filter rewrites a response body, which is only ever correct
        // on a read. An action with no method constraint answers every verb, and one carrying
        // both [HttpGet] and [HttpPost] cannot be filtered per-verb; reject both.
        var methods = action.Selectors
            .SelectMany(selector => selector.ActionConstraints.OfType<HttpMethodActionConstraint>())
            .SelectMany(constraint => constraint.HttpMethods)
            .ToArray();

        return methods.Length > 0
            && Array.TrueForAll(methods, method => HttpMethods.IsGet(method) || HttpMethods.IsHead(method));
    }
}
