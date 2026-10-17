namespace Ankus.CompilerServices;

public static unsafe partial class NativeBackend
{
    /// <summary>
    /// Invokes a managed callback with configuration settings applied at a new PostgreSQL nest level.
    /// </summary>
    /// <param name="callback">The exception-containing managed entry point.</param>
    /// <param name="state">The callback's synchronous managed handle.</param>
    /// <param name="settings">Alternating setting names and values.</param>
    internal static void RunWithSettings(nint callback, nint state, ReadOnlySpan<SpiParameter> settings)
    {
        CheckAccess();
        var request = new NativeSpiRequest
        {
            _operation = SpiOperation.Settings,
            _callback = callback,
            _callbackState = state,
        };
        NativeSpiResult result = default;
        InvokeParameters(&request, settings, &result);
    }
}
