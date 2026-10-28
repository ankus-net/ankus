using System;

namespace Ankus;

/// <summary>
/// Exports a static PgHeapTuple-returning method with one PgTriggerContext parameter as a PostgreSQL trigger function.
/// Return null to skip a row in a BEFORE or INSTEAD OF trigger. Use PgFunction for ordinary SQL declaration options.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class PgTriggerAttribute : Attribute;
