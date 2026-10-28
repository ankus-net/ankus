using System;

namespace Ankus;

/// <summary>
/// Exports a static void method with one PgEventTriggerContext parameter as a PostgreSQL event trigger function.
/// Use PgFunction for SQL declaration options and CREATE EVENT TRIGGER to select events and command filters.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class PgEventTriggerAttribute : Attribute;
