// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System.IO;
using System.Runtime.InteropServices;

namespace IntroSkipper.Helper
{
    /// <summary>
    /// Provides methods to determine the operating system.
    /// </summary>
    public static class OperatingSystem
    {
        /// <summary>
        /// Determines if the current operating system is Windows.
        /// </summary>
        /// <returns>True if the current operating system is Windows; otherwise, false.</returns>
        public static bool IsWindows() =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        /// <summary>
        /// Determines if the current operating system is macOS.
        /// </summary>
        /// <returns>True if the current operating system is macOS; otherwise, false.</returns>
        public static bool IsMacOS() =>
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        /// <summary>
        /// Determines if the current operating system is Linux.
        /// </summary>
        /// <returns>True if the current operating system is Linux; otherwise, false.</returns>
        public static bool IsLinux() =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        /// <summary>
        /// Determines if the current environment is running in Docker.
        /// </summary>
        /// <returns>True if running in a Docker container; otherwise, false.</returns>
        public static bool IsDocker() =>
            File.Exists("/.dockerenv") || File.Exists("/run/.containerenv");
    }
}
