// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Reflection;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Model.Entities;

/// <summary>
/// <see cref="IChapterManager"/> proxy whose <c>GetChapters</c> returns a fixed list or
/// per-item result (including <see langword="null"/>) and counts its calls.
/// </summary>
internal class ChapterManagerStub : DispatchProxy
{
    private Func<Guid, IReadOnlyList<ChapterInfo>?> _getChapters = _ => null;

    public int GetChaptersCallCount { get; private set; }

    public static IChapterManager Create(params ChapterInfo[] chapters) => Create(chapters, out _);

    public static IChapterManager Create(IReadOnlyList<ChapterInfo>? chapters, out ChapterManagerStub stub)
        => CreateForItems(_ => chapters, out stub);

    public static IChapterManager CreateForItems(Func<Guid, IReadOnlyList<ChapterInfo>?> getChapters, out ChapterManagerStub stub)
    {
        var chapterManager = Create<IChapterManager, ChapterManagerStub>();
        stub = (ChapterManagerStub)(object)chapterManager;
        stub._getChapters = getChapters;
        return chapterManager;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IChapterManager.GetChapters))
        {
            GetChaptersCallCount++;
            return _getChapters((Guid)args![0]!);
        }

        throw new NotImplementedException(targetMethod?.Name);
    }
}
