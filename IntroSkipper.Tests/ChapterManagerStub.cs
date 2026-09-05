// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Reflection;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Model.Entities;

/// <summary>
/// <see cref="IChapterManager"/> proxy whose <c>GetChapters</c> returns a fixed list (or
/// <see langword="null"/>, as Jellyfin does for items without chapters) and counts its calls.
/// </summary>
internal class ChapterManagerStub : DispatchProxy
{
    private IReadOnlyList<ChapterInfo>? _chapters;

    public int GetChaptersCallCount { get; private set; }

    public static IChapterManager Create(params ChapterInfo[] chapters) => Create(chapters, out _);

    public static IChapterManager Create(IReadOnlyList<ChapterInfo>? chapters, out ChapterManagerStub stub)
    {
        var chapterManager = Create<IChapterManager, ChapterManagerStub>();
        stub = (ChapterManagerStub)(object)chapterManager;
        stub._chapters = chapters;
        return chapterManager;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IChapterManager.GetChapters))
        {
            GetChaptersCallCount++;
            return _chapters;
        }

        throw new NotImplementedException(targetMethod?.Name);
    }
}
