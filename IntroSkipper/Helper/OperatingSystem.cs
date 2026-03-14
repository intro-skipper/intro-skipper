// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System;
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

        /// <summary>
        /// Gets the name of the current operating system.
        /// </summary>
        /// <returns>The name of the operating system.</returns>
        public static string DetermineOperatingSystem()
        {
            if (IsWindows())
            {
                return "Windows";
            }
            else if (IsMacOS())
            {
                return "macOS";
            }
            else if (IsLinux())
            {
                if (IsDocker())
                {
                    if (Environment.GetEnvironmentVariable("ATTACHED_DEVICES_PERMS") != null)
                    {
                        return "LinuxServer.io image (Docker)";
                    }

                    if (Environment.GetEnvironmentVariable("WEBUI_PORTS") != null)
                    {
                        return "hotio image (Docker)";
                    }

                    return "Linux (Docker)";
                }

                return RuntimeInformation.OSDescription;
            }

            return "Unknown";
        }
    }
}
