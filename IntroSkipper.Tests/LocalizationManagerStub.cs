// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Reflection;
using MediaBrowser.Model.Globalization;

/// <summary>
/// <see cref="ILocalizationManager"/> proxy that answers the English chapter name format, which
/// the embedded chapter read names untitled chapters with. Every other member throws.
/// </summary>
internal class LocalizationManagerStub : DispatchProxy
{
    public static ILocalizationManager Create() => Create<ILocalizationManager, LocalizationManagerStub>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => targetMethod?.Name == nameof(ILocalizationManager.GetServerLocalizedString) && args is ["ChapterNameValue"]
            ? "Chapter {0}"
            : throw new NotImplementedException(targetMethod?.Name);
}
