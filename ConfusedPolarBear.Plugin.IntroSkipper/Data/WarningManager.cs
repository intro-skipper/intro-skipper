namespace ConfusedPolarBear.Plugin.IntroSkipper;

/// <summary>
/// Warning manager.
/// </summary>
public static class WarningManager
{
    private static PluginWarning warnings;

    /// <summary>
    /// Set warning.
    /// </summary>
    /// <param name="warning">Warning.</param>
    public static void SetFlag(PluginWarning warning)
    {
        warnings |= warning;
    }

    /// <summary>
    /// Clear warnings.
    /// </summary>
    public static void Clear()
    {
        warnings = PluginWarning.None;
    }

    /// <summary>
    /// Get warnings.
    /// </summary>
    /// <returns>Warnings.</returns>
    public static string GetWarnings()
    {
        return warnings.ToString();
    }

    /// <summary>
    /// Check if a specific warning flag is set.
    /// </summary>
    /// <param name="warning">Warning flag to check.</param>
    /// <returns>True if the flag is set, otherwise false.</returns>
    public static bool HasFlag(PluginWarning warning)
    {
        return (warnings & warning) == warning;
    }
}
