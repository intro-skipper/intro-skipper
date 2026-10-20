// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Reflection;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Model.Entities;

/// <summary>
/// <see cref="IChapterManager"/> proxy whose <c>GetChapters</c> returns one fixed list for
/// every item, or throws one exception for every item. It stands in for Jellyfin's chapter
/// table in full season passes, the only code that reads it.
/// </summary>
internal class ChapterManagerStub : DispatchProxy
{
    private IReadOnlyList<ChapterInfo> _chapters = [];
    private Exception? _failure;

    public static IChapterManager Create(IReadOnlyList<ChapterInfo> chapters)
    {
        var chapterManager = Create<IChapterManager, ChapterManagerStub>();
        ((ChapterManagerStub)(object)chapterManager)._chapters = chapters;
        return chapterManager;
    }

    public static IChapterManager Throwing(Exception failure)
    {
        var chapterManager = Create<IChapterManager, ChapterManagerStub>();
        ((ChapterManagerStub)(object)chapterManager)._failure = failure;
        return chapterManager;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => targetMethod?.Name == nameof(IChapterManager.GetChapters)
            ? _failure is null ? _chapters : throw _failure
            : throw new NotImplementedException(targetMethod?.Name);
}
