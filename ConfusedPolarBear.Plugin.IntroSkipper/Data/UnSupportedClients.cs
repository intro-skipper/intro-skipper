using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace ConfusedPolarBear.Plugin.IntroSkipper;

/// <summary>
/// List of unsupported clients.
/// </summary>
public static class UnSupportedClients
{
    private static List<string> unSupportedClientsList = new();

    /// <summary>
    /// Check if a client is supported.
    /// </summary>
    /// <param name="clientName">Client name.</param>
    /// <returns>True if the client is supported, otherwise false.</returns>
    public static bool IsClientSupported(string clientName)
    {
        return !unSupportedClientsList.Contains(clientName);
    }

    /// <summary>
    /// Initialize the list of unsupported clients.
    /// </summary>
    public static void InitializeList()
    {
        unSupportedClientsList = ["Android TV", "Kodi"];
        unSupportedClientsList.AddRange(Plugin.Instance!.Configuration.SelectedClients
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList());
    }
}
