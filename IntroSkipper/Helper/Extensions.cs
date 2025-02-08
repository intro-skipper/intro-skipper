namespace IntroSkipper.Helper;

/// <summary>
/// Extension methods for common operations.
/// </summary>
public static class Extensions
{
    /// <summary>
    /// Converts a (bool, T) tuple into a TryXXX pattern with an out parameter.
    /// </summary>
    /// <typeparam name="T">The type of the value parameter.</typeparam>
    /// <param name="tuple">The tuple containing the success flag and value.</param>
    /// <param name="value">When this method returns, contains the value from the tuple if the first element was true, or default if false.</param>
    /// <returns>The success flag from the tuple.</returns>
    public static bool TryOut<T>(this (bool Success, T Value) tuple, out T value)
    {
        (bool success, value) = tuple;
        return success;
    }
}
