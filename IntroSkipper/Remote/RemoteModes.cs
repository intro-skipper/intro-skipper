// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using SegmentScanner.Contracts;

namespace IntroSkipper.Remote;

/// <summary>Converts the stable wire values without assuming plugin enum ordering.</summary>
internal static class RemoteModes
{
    internal static RemoteAnalysisMode ToRemote(AnalysisMode mode) => mode switch
    {
        AnalysisMode.Introduction => RemoteAnalysisMode.Introduction,
        AnalysisMode.Credits => RemoteAnalysisMode.Credits,
        AnalysisMode.Recap => RemoteAnalysisMode.Recap,
        AnalysisMode.Preview => RemoteAnalysisMode.Preview,
        AnalysisMode.Commercial => RemoteAnalysisMode.Commercial,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    internal static AnalysisMode ToPlugin(RemoteAnalysisMode mode) => mode switch
    {
        RemoteAnalysisMode.Introduction => AnalysisMode.Introduction,
        RemoteAnalysisMode.Credits => AnalysisMode.Credits,
        RemoteAnalysisMode.Recap => AnalysisMode.Recap,
        RemoteAnalysisMode.Preview => AnalysisMode.Preview,
        RemoteAnalysisMode.Commercial => AnalysisMode.Commercial,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}
