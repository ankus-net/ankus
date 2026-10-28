using System;

namespace Ankus;

/// <summary>
/// Runs a static method when PostgreSQL first loads the extension in a backend process.
/// </summary>
/// <remarks>
/// Apply this attribute to one accessible, synchronous, non-generic, parameterless static void method per assembly.
/// Initialization runs before the first extension function and can run again if an earlier initialization failed.
/// Any PgModuleLoad registration completes first. Initialization waits for parallel-worker state restoration where required;
/// use PgModuleLoad to install native hooks before a worker's first executor entry.
/// PostgreSQL APIs that require a transaction are available only when the library is loaded inside a transaction.
/// With shared_preload_libraries, initialization runs before PostgreSQL creates backend processes.
/// Each backend receives an independent copy of the initialized managed state.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PgInitializeAttribute : Attribute;
