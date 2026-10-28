using System;

namespace Ankus;

/// <summary>
/// Exports a static method as the library's logical decoding output plugin initializer, <c>_PG_output_plugin_init</c>.
/// </summary>
/// <remarks>
/// PostgreSQL loads an output plugin by library name, for example in
/// <c>pg_create_logical_replication_slot('slot', 'MyExtension')</c>, and passes the plugin's callback table to this
/// export. Declare one synchronous static method returning void with a single by-value
/// <c>Ankus.Postgres.OutputPluginCallbacks*</c> parameter, and assign the callbacks the plugin implements, typically
/// from <see cref="PgNativeCallbackAttribute"/> properties. The method runs beneath the native callback guard: exceptions
/// unwind its managed frames before PostgreSQL reports an error. The table belongs to PostgreSQL; do not retain its
/// address. An extension declares at most one initializer.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class PgOutputPluginAttribute : Attribute;
