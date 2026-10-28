using System;
using System.ComponentModel;

namespace Ankus.CompilerServices;

/// <summary>
/// Copies event trigger metadata and tracks nested managed event invocation scopes on their owning threads.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class NativeEventTrigger
{
    [ThreadStatic]
    private static PgEventTriggerContext? s_current;

    /// <summary>
    /// Copies exactly two borrowed native text slots and enters the new event invocation after validation succeeds.
    /// The caller retains ownership of the input buffers and must pair a successful entry with Exit in a finally block.
    /// </summary>
    /// <param name="arguments">The event name and command tag, in that order.</param>
    /// <returns>The active context to pass to the generated callback and matching Exit call.</returns>
    public static PgEventTriggerContext Enter(ReadOnlySpan<NativeValue> arguments)
    {
        if (arguments.Length != 2)
        {
            throw new InvalidOperationException("A native event trigger invocation must contain exactly two context slots.");
        }

        var context = new PgEventTriggerContext(arguments[0].ReadEventTriggerText(), arguments[1].ReadEventTriggerText(), s_current);
        s_current = context;
        return context;
    }

    /// <summary>
    /// Restores the enclosing event context after the matching callback finishes, including exceptional exits.
    /// </summary>
    /// <param name="context">The current context returned by Enter.</param>
    public static void Exit(PgEventTriggerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!ReferenceEquals(s_current, context))
        {
            throw new InvalidOperationException("Event trigger scopes must exit in stack order on their owning thread.");
        }

        s_current = context.DetachParent();
    }

    /// <summary>
    /// Requires the exact active invocation and the phase that owns the requested PostgreSQL metadata helper.
    /// </summary>
    internal static void CheckAccess(PgEventTriggerContext context, PgEventTriggerKind kind)
    {
        if (!ReferenceEquals(s_current, context))
        {
            throw new InvalidOperationException("Event trigger metadata requires its active invocation on the owning backend thread.");
        }

        if (context.Kind != kind)
        {
            throw new InvalidOperationException("The requested metadata is unavailable in this event trigger phase.");
        }
    }
}
