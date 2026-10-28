using System;
using System.Collections.Generic;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Retains provisional native result owners until every requested SPI column has converted successfully.
/// </summary>
internal sealed class SpiConversionScope : IDisposable
{
    private readonly NativeRelationScope _relations = new();
    private readonly List<IDisposable> _views = [];

    /// <summary>
    /// Records a newly converted relation or borrowed view without taking ownership of other managed values.
    /// </summary>
    /// <typeparam name="T">The requested result representation.</typeparam>
    /// <param name="value">The converted result, including SQL NULL.</param>
    /// <returns>The same result.</returns>
    internal T Add<T>(T value)
    {
        _relations.Add(value);
        IDisposable? view = value switch
        {
            IPgArrayView array => array,
            PgByteaView bytes => bytes,
            PgTextView text => text,
            PgCStringView text => text,
            _ => null,
        };
        if (view is not null)
        {
            try
            {
                _views.Add(view);
            }
            catch (Exception primary)
            {
                PgResultCleanup.Dispose(view, primary);
                throw;
            }
        }

        return value;
    }

    /// <summary>
    /// Leaves all successfully converted native values owned by the caller.
    /// </summary>
    internal void Relinquish()
    {
        _views.Clear();
        _relations.Relinquish();
    }

    /// <summary>
    /// Attempts every provisional close while retaining cleanup diagnostics on the original conversion failure.
    /// </summary>
    /// <param name="primary">The conversion or temporary-result cleanup failure being propagated.</param>
    internal void ReleaseAfterFailure(Exception primary)
    {
        List<Exception>? failures = ReleaseViews();
        _relations.ReleaseAfterFailure(primary);
        if (failures is not null)
        {
            primary.Data["Ankus.SpiViewCleanup"] = new AggregateException("SPI view cleanup failed.", failures);
        }
    }

    /// <summary>
    /// Releases all untransferred native values and reports cleanup failures after every close has been attempted.
    /// </summary>
    public void Dispose()
    {
        List<Exception>? failures = ReleaseViews();
        try
        {
            _relations.Dispose();
        }
        // codeql[cs/catch-of-all-exceptions] Every cleanup step still runs, and all of their failures are reported together.
        catch (Exception cleanup)
        {
            (failures ??= []).Add(cleanup);
        }

        if (failures is not null)
        {
            throw new AggregateException("SPI conversion cleanup failed.", failures);
        }
    }

    /// <summary>
    /// Closes every provisional view in reverse acquisition order and preserves each failure.
    /// </summary>
    private List<Exception>? ReleaseViews()
    {
        List<Exception>? failures = null;
        for (int index = _views.Count - 1; index >= 0; index--)
        {
            try
            {
                _views[index].Dispose();
            }
            // codeql[cs/catch-of-all-exceptions] Every cleanup step still runs, and all of their failures are reported together.
            catch (Exception cleanup)
            {
                (failures ??= []).Add(cleanup);
            }
        }

        _views.Clear();
        return failures;
    }
}
