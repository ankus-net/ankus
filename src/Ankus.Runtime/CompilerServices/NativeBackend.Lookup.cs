using System;
using System.Collections.Generic;
using System.Text;

namespace Ankus.CompilerServices;

public static partial class NativeBackend
{
    /// <summary>
    /// Copies a complete function catalog snapshot and releases all native result storage on every path.
    /// </summary>
    internal static unsafe PgFunctionInfo? FunctionInfo(uint oid)
    {
        CheckAccess();
        var request = new NativeSpiRequest { _operation = SpiOperation.Lookup, _scalarOperation = 3 };
        NativeSpiResult result = default;
        try
        {
            InvokeParameters(&request, [SpiParameter.Create(oid)], &result);
            if (result._rowCount == 0)
            {
                return null;
            }

            if (result._rowCount != 1 || result._columnCount != 24 || result._values == null)
            {
                throw new InvalidOperationException("Invalid function catalog result.");
            }

            return new PgFunctionInfo(oid, new ReadOnlySpan<NativeValue>(result._values, result._columnCount));
        }
        finally
        {
            ReleaseResult(&result);
        }
    }

    private static readonly UTF8Encoding s_lookupEncoding = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Reads the major version from the selected native PostgreSQL headers under the backend guard.
    /// </summary>
    internal static int GetPostgresMajor() => Scalar<int>(SpiOperation.Lookup, 2, []);

    /// <summary>
    /// Rejects string content that would lose identity when transported as a native C string.
    /// </summary>
    internal static void ValidateLookupName(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("PostgreSQL names cannot contain a zero character.", parameterName);
        }

        _ = s_lookupEncoding.GetByteCount(value);
    }

    /// <summary>
    /// Calls native regtypein with exact type syntax under the existing error and scratch-context guard.
    /// </summary>
    internal static uint ResolveTypeName(string typeName)
    {
        ValidateLookupName(typeName, nameof(typeName));
        return Scalar<uint>(SpiOperation.Lookup, 0, [SpiParameter.Create(typeName)]);
    }

    /// <summary>
    /// Passes exact OIDs followed by independent name components to native OpernameGetOprid.
    /// </summary>
    internal static uint ResolveOperator(IReadOnlyList<string> components, uint leftTypeOid, uint rightTypeOid)
    {
        SpiParameter[] parameters = new SpiParameter[checked(components.Count + 2)];
        parameters[0] = SpiParameter.Create(leftTypeOid);
        parameters[1] = SpiParameter.Create(rightTypeOid);
        for (int index = 0; index < components.Count; index++)
        {
            parameters[index + 2] = SpiParameter.Create(components[index]);
        }

        return Scalar<uint>(SpiOperation.Lookup, 1, parameters);
    }
}
