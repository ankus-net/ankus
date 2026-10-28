using System;
using System.Collections.Generic;

namespace Ankus;

/// <summary>
/// Releases provisional native wrappers when a collection conversion cannot return its owners.
/// </summary>
internal static class VarlenaCleanup
{
    /// <summary>
    /// Attempts every provisional release and preserves the original conversion failure alongside cleanup errors.
    /// </summary>
    internal static void Release<T>(ReadOnlySpan<T> values, Exception primary)
    {
        List<Exception>? failures = null;
        foreach (T value in values)
        {
            if (value is IPgVarlena wrapper)
            {
                try
                {
                    wrapper.Dispose();
                }
                // codeql[cs/catch-of-all-exceptions] Every cleanup step still runs, and all of their failures are reported together.
                catch (Exception cleanup)
                {
                    failures ??= [primary];
                    failures.Add(cleanup);
                }
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("Converting PostgreSQL varlena values and releasing provisional allocations failed.", failures);
        }
    }
}
