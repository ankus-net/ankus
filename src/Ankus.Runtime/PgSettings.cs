using System;
using System.Collections.Generic;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Changes PostgreSQL configuration settings for the duration of a callback.
/// </summary>
/// <remarks>
/// PostgreSQL applies the values as it does a function's <c>SET</c> clause: at a new nest level, with the current
/// user's privileges, and restored when the callback returns or throws. When a PostgreSQL error aborts the enclosing
/// transaction or subtransaction, that rollback restores them instead. A plain <c>SET</c> inside the callback outlasts
/// it, as it does inside such a function.
/// </remarks>
public static class PgSettings
{
    /// <summary>
    /// Runs synchronous work with one setting changed.
    /// </summary>
    /// <param name="name">The setting's name, such as <c>lock_timeout</c>.</param>
    /// <param name="value">The value, in the text form <c>SET</c> accepts.</param>
    /// <param name="action">The work to run on the active backend thread.</param>
    /// <remarks>See <see cref="Run{TResult}(IEnumerable{KeyValuePair{string, string}}, Func{TResult})"/>.</remarks>
    public static void Run(string name, string value, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _ = Run(name, value, () =>
        {
            action();
            return 0;
        });
    }

    /// <summary>
    /// Runs synchronous work with one setting changed and returns its result.
    /// </summary>
    /// <typeparam name="TResult">The callback result type.</typeparam>
    /// <param name="name">The setting's name, such as <c>lock_timeout</c>.</param>
    /// <param name="value">The value, in the text form <c>SET</c> accepts.</param>
    /// <param name="action">The work to run on the active backend thread.</param>
    /// <returns>The callback's result.</returns>
    /// <remarks>See <see cref="Run{TResult}(IEnumerable{KeyValuePair{string, string}}, Func{TResult})"/>.</remarks>
    public static TResult Run<TResult>(string name, string value, Func<TResult> action)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);
        return Run([new(name, value)], action);
    }

    /// <summary>
    /// Runs synchronous work with several settings changed.
    /// </summary>
    /// <param name="settings">The setting names and values, applied in order.</param>
    /// <param name="action">The work to run on the active backend thread.</param>
    /// <remarks>See <see cref="Run{TResult}(IEnumerable{KeyValuePair{string, string}}, Func{TResult})"/>.</remarks>
    public static void Run(IEnumerable<KeyValuePair<string, string>> settings, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _ = Run(settings, () =>
        {
            action();
            return 0;
        });
    }

    /// <summary>
    /// Runs synchronous work with several settings changed and returns its result.
    /// </summary>
    /// <typeparam name="TResult">The callback result type.</typeparam>
    /// <param name="settings">The setting names and values, applied in order.</param>
    /// <param name="action">The work to run on the active backend thread.</param>
    /// <returns>The callback's result.</returns>
    /// <remarks>
    /// PostgreSQL checks every name, value and privilege before the callback runs. A rejected one throws
    /// <see cref="PgException"/>, such as SQLSTATE <c>42704</c> for an unknown setting or <c>22023</c> for an invalid
    /// value, and leaves the transaction usable. Where PostgreSQL cannot start a subtransaction, such as in a
    /// transaction callback, a rejection requires rollback like any other error there. Values use the text form of
    /// <c>SET</c>, including units such as <c>100ms</c>. Do not perform transaction control or asynchronous work in the
    /// callback.
    /// </remarks>
    public static TResult Run<TResult>(IEnumerable<KeyValuePair<string, string>> settings, Func<TResult> action)
        => NativeSettings.Run(settings, action);
}
