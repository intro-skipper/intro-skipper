// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Runtime.InteropServices;

namespace IntroSkipper.Helper
{
    /// <summary>
    /// Names the host operating system for the support bundle.
    /// </summary>
    internal static class OperatingSystem
    {
        /// <summary>
        /// Gets the name of the current operating system, distinguishing the common Docker images on Linux.
        /// </summary>
        /// <returns>The name of the operating system.</returns>
        public static string DetermineOperatingSystem()
        {
            if (System.OperatingSystem.IsWindows())
            {
                return "Windows";
            }

            if (System.OperatingSystem.IsMacOS())
            {
                return "macOS";
            }

            if (!System.OperatingSystem.IsLinux())
            {
                return "Unknown";
            }

            if (!File.Exists("/.dockerenv") && !File.Exists("/run/.containerenv"))
            {
                return RuntimeInformation.OSDescription;
            }

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
    }
}
