using System;

namespace Ankus.CompilerServices;

public static partial class NativeBackend
{
    /// <summary>
    /// Allocates the managed owner before acquiring a native reference and transfers the checked identity without allocation.
    /// </summary>
    internal static unsafe PgRelation? OpenRelation(int operation, uint oid, string? name, PgLockMode mode,
        bool missingOk = false, nint pointer = 0)
    {
        CheckAccess();
        var relation = new PgRelation(s_execute, operation != 3);
        var request = new NativeSpiRequest
        {
            _operation = SpiOperation.Relation,
            _scalarOperation = operation,
            _functionOid = oid,
            _limit = (int)mode,
            _readOnly = missingOk ? (byte)1 : (byte)0,
            _callback = pointer,
        };
        NativeSpiResult result = default;
        try
        {
            InvokeParameters(&request, name is null ? [] : [SpiParameter.Create(name)], &result);
            relation.Identity = result._cursorId;
            return relation.Identity == 0 ? null : relation;
        }
        finally
        {
            ReleaseResult(&result);
        }
    }

    /// <summary>
    /// Reads a scalar relation property after native registry validation.
    /// </summary>
    internal static T ReadRelation<T>(long identity, int operation)
        => ReadRelationValue(identity, operation, static value => SpiRow.Convert<T>(SpiType.FromNative(value, SpiType.GetOid<T>())));

    /// <summary>
    /// Copies a retained relation's actual descriptor, including index and dropped-column metadata.
    /// </summary>
    internal static PgTupleDescriptor RelationDescriptor(long identity)
        => ReadRelationValue(identity, 12, static value => value.ReadRelationDescriptor());

    /// <summary>
    /// Reads the native pointer after checking the live relation token.
    /// </summary>
    internal static nint RelationPointer(long identity)
        => ReadRelationValue(identity, 15, static value => checked((nint)value.Integral));

    /// <summary>
    /// Performs ownership transfer or a statistics update through the native guard.
    /// </summary>
    internal static unsafe void UpdateRelation(long identity, int operation, long count = 0)
    {
        CheckAccess();
        var request = new NativeSpiRequest
        {
            _operation = SpiOperation.Relation,
            _scalarOperation = operation,
            _cursorId = identity,
            _sessionId = 0,
        };
        NativeSpiResult result = default;
        try
        {
            InvokeParameters(&request, operation == 20 ? [SpiParameter.Create(count)] : [], &result);
        }
        finally
        {
            ReleaseResult(&result);
        }
    }

    /// <summary>
    /// Releases a relation without starting a subtransaction, including during executor cleanup.
    /// </summary>
    internal static unsafe void CloseRelation(long identity)
    {
        CheckDisposalAccess();
        var request = new NativeSpiRequest
        {
            _operation = SpiOperation.Relation,
            _cursorId = identity,
            _cleanupOnly = 1,
        };
        NativeSpiResult result = default;
        try
        {
            Invoke(&request, &result);
        }
        finally
        {
            ReleaseResult(&result);
        }
    }

    private static unsafe T ReadRelationValue<T>(long identity, int operation, Func<NativeValue, T> read)
    {
        CheckAccess();
        var request = new NativeSpiRequest
        {
            _operation = SpiOperation.Relation,
            _scalarOperation = operation,
            _cursorId = identity,
        };
        NativeSpiResult result = default;
        try
        {
            Invoke(&request, &result);
            return read(result._text);
        }
        finally
        {
            ReleaseResult(&result);
        }
    }
}
