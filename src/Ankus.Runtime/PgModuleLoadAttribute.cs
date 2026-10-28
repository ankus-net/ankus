using System;

namespace Ankus;

/// <summary>
/// Registers native extension hooks and providers when PostgreSQL loads the library.
/// </summary>
/// <remarks>
/// Apply this attribute to one accessible, synchronous, non-generic, parameterless static void method per assembly.
/// The method runs before PgInitialize, including before a parallel worker's first executor entry.
/// SQL is unavailable while PostgreSQL restores worker state or during postmaster startup.
/// Use PgInitialize for work that requires restored worker settings and transaction state.
/// Failed registration can be retried; managed state and installed hooks are not automatically rolled back.
/// A retry can start from a later load or from the next invocation of a callback that the failed attempt installed.
/// Record each hook installation as soon as it succeeds so that a retry does not save the hook as its own predecessor.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PgModuleLoadAttribute : Attribute;
