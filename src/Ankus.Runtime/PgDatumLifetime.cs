using System;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Checks a datum's extension provider, context identity, and reset generation before native access.
/// </summary>
internal sealed class PgDatumLifetime
{
    private readonly nint _provider;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly PgDatumLifetime? _source;
    private readonly NativeBorrowedBuffer? _view;
    private bool _captured;

    /// <summary>
    /// Captures the generation of a live context.
    /// </summary>
    /// <param name="context">The native storage lifetime anchor.</param>
    /// <param name="scope">The optional callback lease required in addition to the native owner.</param>
    /// <param name="source">The optional source whose bytes remain borrowed by this owner.</param>
    internal PgDatumLifetime(PgMemoryContext context, NativeBorrowScope? scope = null, PgDatumLifetime? source = null)
    {
        source?.Validate();
        ObjectDisposedException.ThrowIf(!context.IsAlive, context);
        _provider = NativeMemoryContext.Provider;
        ContextId = context.Id;
        NativeMemoryRequest request = new()
        {
            _operation = NativeMemoryOperation.CaptureGeneration,
            _context = ContextId
        };
        NativeMemoryContext.Invoke(ref request, out NativeMemoryResult result);
        Generation = unchecked((nuint)result._value);
        Scope = scope ?? source?.Scope;
        _source = source;
    }

    /// <summary>
    /// Captures a known live context's identity and generation.
    /// </summary>
    private PgDatumLifetime(nint provider, nint contextId, nuint generation, NativeBorrowScope scope)
    {
        _provider = provider;
        ContextId = contextId;
        Generation = generation;
        Scope = scope;
    }

    /// <summary>
    /// Captures the callback's current context and its generation in one native call, for a borrowed input.
    /// </summary>
    /// <param name="scope">The callback lease the input expires with.</param>
    /// <returns>The input's lifetime.</returns>
    /// <remarks>
    /// Every caller wraps the input in a view whose constructor checks the lifetime before anything else, so that
    /// first check can trust the generation just read without reading it again.
    /// </remarks>
    internal static PgDatumLifetime ForCurrentInput(NativeBorrowScope scope)
    {
        NativeMemoryRequest request = new() { _operation = NativeMemoryOperation.Current };
        NativeMemoryContext.Invoke(ref request, out NativeMemoryResult result);
        nint provider = NativeMemoryContext.Provider;
        PgDatumLifetime lifetime = result._value == 0
            ? new PgDatumLifetime(PgMemoryContext.FromId(provider, result._context), scope)
            : new PgDatumLifetime(provider, result._context, unchecked((nuint)result._value), scope);
        lifetime._captured = true;
        return lifetime;
    }

    /// <summary>
    /// Derives a lifetime for a view that borrows its source's storage in place, which also ends when the view is
    /// disposed, as a view with private storage ends when that storage is freed.
    /// </summary>
    /// <param name="source">The source whose storage the view borrows.</param>
    /// <param name="view">The view whose disposal ends this lifetime.</param>
    internal PgDatumLifetime(PgDatumLifetime source, NativeBorrowedBuffer view)
    {
        _provider = source._provider;
        _thread = source._thread;
        ContextId = source.ContextId;
        Generation = source.Generation;
        Scope = source.Scope;
        _source = source;
        _view = view;
    }

    /// <summary>
    /// Gets the callback lease inherited by borrowed inputs and their derived elements.
    /// </summary>
    internal NativeBorrowScope? Scope { get; }

    /// <summary>
    /// Gets the registered context identity.
    /// </summary>
    internal nint ContextId { get; }

    /// <summary>
    /// Gets the reset generation captured for the datum's storage.
    /// </summary>
    internal nuint Generation { get; }

    /// <summary>
    /// Rejects a foreign thread before native validation or resource disposal.
    /// </summary>
    internal void CheckThread()
    {
        if (_thread != Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException("A PostgreSQL datum must remain on its originating backend thread.");
        }
    }

    /// <summary>
    /// Rejects use from another thread or another extension's backend capability, without checking the owner's generation.
    /// </summary>
    internal void CheckAccess()
    {
        CheckThread();
        NativeMemoryContext.CheckProvider(_provider);
    }

    /// <summary>
    /// Rejects stale context generations and access from another backend provider or thread.
    /// </summary>
    internal void Validate()
    {
        for (PgDatumLifetime? current = this; current is not null; current = current._source)
        {
            current.ValidateContext();
        }
    }

    /// <summary>
    /// Checks one owner without assuming its parent's generation is unchanged while its child survives.
    /// </summary>
    private void ValidateContext()
    {
        CheckThread();
        if (_view is not null)
        {
            // The source, next in the chain, checks the shared native owner.
            ObjectDisposedException.ThrowIf(_view.IsReleased, typeof(PgDatum));
            return;
        }

        Scope?.Validate();
        NativeMemoryContext.CheckProvider(_provider);
        if (_captured)
        {
            // The generation was read for this input a moment ago; later checks read it again.
            _captured = false;
            return;
        }

        NativeMemoryRequest request = new()
        {
            _operation = NativeMemoryOperation.CaptureGeneration,
            _context = ContextId
        };
        try
        {
            NativeMemoryContext.Invoke(ref request, out NativeMemoryResult result);
            if (unchecked((nuint)result._value) != Generation)
            {
                throw new ObjectDisposedException(nameof(PgDatum), "The datum's memory context has been reset.");
            }
        }
        catch (PgException exception) when (exception.SqlState == "55000")
        {
            throw new ObjectDisposedException(nameof(PgDatum), "The datum's memory context has been deleted.");
        }
    }
}
