// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Reflection;
using IntroSkipper.Controllers;
using IntroSkipper.Filters;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Xunit;

/// <summary>
/// Tests for <see cref="MediaSegmentsFilterConvention"/>. The convention attaches the
/// premiere-intro response filter, which rewrites response bodies, so its match must never
/// reach a controller it does not own. Attaching to the wrong controller is undetectable at
/// runtime, so the gates are pinned here instead.
/// </summary>
public sealed class MediaSegmentsFilterConventionTests
{
    [Theory]
    // Jellyfin's own controller: the single intended target.
    [InlineData("MediaSegmentsController", "Jellyfin.Api", true)]
    // This plugin's controller renamed into the pattern must still not match.
    [InlineData("MediaSegmentsController", "IntroSkipper", false)]
    // A third-party plugin serving the same route shapes must not have its responses rewritten.
    [InlineData("MediaSegmentsController", "Jellyfin.Plugin.MediaSegmentsApi", false)]
    // Pins an exact match rather than a substring one.
    [InlineData("MediaSegmentsApiController", "Jellyfin.Api", false)]
    [InlineData("SegmentEditorController", "Jellyfin.Api", false)]
    [InlineData("MediaSegmentsController", null, false)]
    public void IsCoreMediaSegmentsController_MatchesOnlyJellyfinsOwnController(
        string controllerTypeName,
        string? assemblyName,
        bool expected)
        => Assert.Equal(
            expected,
            MediaSegmentsFilterConvention.IsCoreMediaSegmentsController(controllerTypeName, assemblyName));

    [Theory]
    [InlineData("itemId", true)]
    [InlineData("ItemId", true)]
    // The filter also reads an "id" route value, but the convention deliberately does not widen
    // to it: the filter's key set is a harmless superset, this gate is not.
    [InlineData("id", false)]
    [InlineData("seriesId", false)]
    public void ShouldFilterAction_RequiresAnItemIdParameter(string parameterName, bool expected)
    {
        var action = CreateAction(parameterName, "GET");

        Assert.Equal(expected, MediaSegmentsFilterConvention.ShouldFilterAction(action));
    }

    [Fact]
    public void ShouldFilterAction_RejectsAnActionWithoutParameters()
    {
        var action = CreateAction(parameterName: null, "GET");

        Assert.False(MediaSegmentsFilterConvention.ShouldFilterAction(action));
    }

    [Theory]
    [InlineData(true, "GET")]
    [InlineData(true, "HEAD")]
    [InlineData(true, "GET", "HEAD")]
    [InlineData(false, "POST")]
    [InlineData(false, "PUT")]
    [InlineData(false, "DELETE")]
    // Cannot be filtered per-verb, so the whole action is rejected.
    [InlineData(false, "GET", "POST")]
    public void ShouldFilterAction_AttachesToReadsOnly(bool expected, params string[] httpMethods)
    {
        var action = CreateAction("itemId", httpMethods);

        Assert.Equal(expected, MediaSegmentsFilterConvention.ShouldFilterAction(action));
    }

    [Fact]
    public void ShouldFilterAction_RejectsAnActionWithNoMethodConstraint()
    {
        // No constraint means the action answers every verb, writes included.
        var action = CreateAction("itemId");

        Assert.False(MediaSegmentsFilterConvention.ShouldFilterAction(action));
    }

    /// <summary>
    /// End to end through the real convention, using the real controller type. The second row
    /// hands the model Jellyfin's controller name, which is what the original name-substring
    /// match keyed on, so this row fails if that match is ever reintroduced. It passes today
    /// because the editor controller is declared in this plugin's assembly, not Jellyfin's.
    /// </summary>
    /// <param name="controllerName">The controller name to present to the convention.</param>
    [Theory]
    [InlineData("SegmentEditor")]
    [InlineData("MediaSegments")]
    public void SegmentEditorController_IsNeverGivenThePremiereIntroFilter(string controllerName)
    {
        var typeInfo = typeof(SegmentEditorController).GetTypeInfo();
        var method = typeInfo.GetMethod(nameof(SegmentEditorController.GetSegmentsAsync))!;
        var action = new ActionModel(method, []);
        action.Parameters.Add(new ParameterModel(method.GetParameters()[0], []) { ParameterName = "itemId" });
        action.Selectors.Add(CreateSelector("GET"));
        var controller = new ControllerModel(typeInfo, []) { ControllerName = controllerName };
        controller.Actions.Add(action);
        var application = new ApplicationModel();
        application.Controllers.Add(controller);

        new MediaSegmentsFilterConvention().Apply(application);

        Assert.Empty(action.Filters);
    }

    private static ActionModel CreateAction(string? parameterName, params string[] httpMethods)
    {
        // Any method with one parameter works: the gates read the model, not the reflection
        // metadata, and ParameterModel.ParameterName is what the convention inspects.
        var method = typeof(MediaSegmentsFilterConventionTests).GetMethod(
            nameof(SampleAction),
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var action = new ActionModel(method, []);

        if (parameterName is not null)
        {
            action.Parameters.Add(new ParameterModel(method.GetParameters()[0], []) { ParameterName = parameterName });
        }

        if (httpMethods.Length > 0)
        {
            action.Selectors.Add(CreateSelector(httpMethods));
        }

        return action;
    }

    private static SelectorModel CreateSelector(params string[] httpMethods)
    {
        var selector = new SelectorModel();
        selector.ActionConstraints.Add(new HttpMethodActionConstraint(httpMethods));
        return selector;
    }

    private static void SampleAction(Guid itemId) => _ = itemId;
}
