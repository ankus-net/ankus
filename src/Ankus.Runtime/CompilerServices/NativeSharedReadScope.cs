using System;

namespace Ankus.CompilerServices;

/// <summary>
/// Links stack-resident original-storage admissions and excludes cells under mutable access.
/// </summary>
internal unsafe struct NativeSharedReadScope
{
    [ThreadStatic]
    private static NativeSharedReadScope* s_current;

    private NativeSharedReadScope* _previous;
    private nint _provider;
    private nuint _address;
    private nuint _length;
    private bool _mutable;

    /// <summary>
    /// Publishes a caller-owned stack frame until the matching synchronous read exits.
    /// </summary>
    internal static void Push(NativeSharedReadScope* frame, nint provider, nint address, nuint length)
        => Push(frame, provider, address, length, mutable: false);

    /// <summary>
    /// Prevents inline lock access to a region that the callback can overwrite directly.
    /// </summary>
    internal static void PushMutation(NativeSharedReadScope* frame, nint provider, nint address, nuint length)
        => Push(frame, provider, address, length, mutable: true);

    /// <summary>
    /// Links a readonly admission or mutable exclusion until the matching callback exits.
    /// </summary>
    private static void Push(NativeSharedReadScope* frame, nint provider, nint address, nuint length, bool mutable)
    {
        frame->_previous = s_current;
        frame->_provider = provider;
        frame->_address = (nuint)address;
        frame->_length = length;
        frame->_mutable = mutable;
        s_current = frame;
    }

    /// <summary>
    /// Finds the innermost live admission covering a complete inline cell and its native provider.
    /// </summary>
    internal static nint Find(nint address, nuint length)
    {
        nint provider = NativeMemoryContext.Provider;
        nint admission = 0;
        for (NativeSharedReadScope* frame = s_current; frame != null; frame = frame->_previous)
        {
            if (frame->_mutable && length != 0 && frame->_length != 0 &&
                ((nuint)address >= frame->_address
                    ? (nuint)address - frame->_address < frame->_length
                    : frame->_address - (nuint)address < length))
            {
                throw new InvalidOperationException("Finish mutating the parent value before accessing its inline PostgreSQL spinlocks.");
            }

            if (admission == 0 && !frame->_mutable && frame->_provider == provider && (nuint)address >= frame->_address && length <= frame->_length &&
                (nuint)address - frame->_address <= frame->_length - length)
            {
                admission = (nint)frame;
            }
        }

        return admission != 0 ? admission :
            throw new InvalidOperationException("Access an inline PostgreSQL spinlock directly within its owning scoped read callback.");
    }

    /// <summary>
    /// Releases borrowed locks before unlinking this frame and dropping its address admission.
    /// </summary>
    internal static void Pop(NativeSharedReadScope* frame)
    {
        try
        {
            NativeSpinLockLease.ReleaseFrame((nint)frame);
        }
        finally
        {
            s_current = frame->_previous;
        }
    }
}
