// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using IntroSkipper.Manager;

/// <summary>Settable <see cref="IMediaSegmentMirrorPolicy"/> fake with a manual toggle event.</summary>
internal sealed class FakeMirrorPolicy : IMediaSegmentMirrorPolicy
{
    public event EventHandler<bool>? EnabledChanged;

    public bool Enabled { get; set; } = true;

    internal void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        EnabledChanged?.Invoke(this, enabled);
    }
}
