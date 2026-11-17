using Brutal.Logging;

namespace DeltaVMap.Core;

internal static class LogHelper
{
    private static readonly HashSet<string> _loggedWarnings = new();
    private static readonly HashSet<string> _loggedErrors = new();

    public static void WarnOnce(string key, string message)
    {
        if (_loggedWarnings.Add(key))
            DefaultCategory.Log.Warning(message);
    }

    // For a path that can fail on every frame: the message, such as an exception with its stack
    // trace, is built only when it is logged.
    public static void WarnOnce<TState>(string key, TState state, Func<TState, string> message)
    {
        if (_loggedWarnings.Add(key))
            DefaultCategory.Log.Warning(message(state));
    }

    public static void ErrorOnce(string key, string message)
    {
        if (_loggedErrors.Add(key))
            DefaultCategory.Log.Error(message);
    }

    public static void Reset()
    {
        _loggedWarnings.Clear();
        _loggedErrors.Clear();
    }
}
