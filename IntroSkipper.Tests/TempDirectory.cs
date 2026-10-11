// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.IO;

/// <summary>
/// A fresh directory under the system temp path, deleted with its contents on dispose.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    public DirectoryInfo Info { get; } = Directory.CreateTempSubdirectory("IntroSkipper.Tests-");

    /// <summary>
    /// Gets the path of <paramref name="name"/> inside the directory, without creating it.
    /// </summary>
    public string Join(string name) => Path.Join(Info.FullName, name);

    public void Dispose() => Info.Delete(recursive: true);
}
