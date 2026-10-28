using System;
using System.Linq;

namespace Ankus.CompilerServices;

/// <summary>
/// Checks a complete generated declaration identity against the current native capability.
/// </summary>
internal static unsafe class NativeBindingContract
{
    private static Validation? s_validated;

    /// <summary>
    /// Borrows the exact identity bytes while the backend validates its selected headers and server major.
    /// </summary>
    /// <param name="identity">The generated UTF-8 binding identity.</param>
    /// <param name="postgresMajor">The generated PostgreSQL major version.</param>
    /// <remarks>
    /// The native contract is fixed for a provider, so a contract it has accepted is accepted again without entering it,
    /// provided the guard would admit the check. Otherwise the native check runs and reports exactly what it finds.
    /// </remarks>
    internal static void Validate(ReadOnlySpan<byte> identity, int postgresMajor)
    {
        NativeBorrowScope.CheckBackendAccess();
        NativeMemoryApi* api = NativeMemoryContext.CurrentApi;
        if (NativeMemoryContext.Admits(api) && Accepted(api, identity, postgresMajor))
        {
            return;
        }

        fixed (byte* data = identity)
        {
            NativeMemoryRequest request = new()
            {
                _operation = NativeMemoryOperation.NativeBinding,
                _data = (nint)data,
                _length = (nuint)identity.Length,
                _value = postgresMajor,
            };
            NativeMemoryContext.Invoke(ref request, out _);
        }

        s_validated = new Validation(NativeMemoryContext.Provider, postgresMajor, identity.ToArray());
    }

    /// <summary>
    /// Determines whether a callback's provider already accepted this contract.
    /// </summary>
    /// <param name="api">The callback's native capability.</param>
    /// <param name="identity">The generated UTF-8 binding identity.</param>
    /// <param name="postgresMajor">The generated PostgreSQL major version.</param>
    /// <returns>Whether the contract is known to match; false when it must be checked natively.</returns>
    internal static bool Accepted(NativeMemoryApi* api, ReadOnlySpan<byte> identity, int postgresMajor)
        => s_validated is { } validated && validated.Matches(api->_provider, identity, postgresMajor);

    /// <summary>
    /// Records one contract the native provider accepted.
    /// </summary>
    /// <param name="provider">The native provider that accepted it.</param>
    /// <param name="postgresMajor">The accepted PostgreSQL major version.</param>
    /// <param name="identity">A copy of the accepted identity bytes.</param>
    private sealed class Validation(nint provider, int postgresMajor, byte[] identity)
    {
        /// <summary>
        /// Determines whether a provider would accept the same contract again.
        /// </summary>
        /// <param name="active">The provider to check.</param>
        /// <param name="candidate">The identity to check.</param>
        /// <param name="major">The PostgreSQL major version to check.</param>
        /// <returns>Whether the contract and provider are the ones already accepted.</returns>
        internal bool Matches(nint active, ReadOnlySpan<byte> candidate, int major)
            => major == postgresMajor && active == provider && candidate.SequenceEqual(identity);
    }
}
