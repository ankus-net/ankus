using System;
using System.Collections.Generic;
using System.Threading;

namespace Ankus.CompilerServices;

/// <summary>
/// Assigns run-time configuration parameters with hooks to the native bridge's prebuilt hook slots.
/// </summary>
/// <remarks>
/// PostgreSQL's hook signatures carry no parameter identity, so the bridge has one set of hook functions per slot and
/// passes the slot to the managed dispatcher. Slot zero means a parameter without hooks.
/// </remarks>
internal static class RuntimeGucHooks
{
    /// <summary>
    /// The number of slots the native bridge provides.
    /// </summary>
    internal const int Capacity = 64;

    private static readonly Lock s_gate = new();
    private static readonly Entry?[] s_slots = new Entry?[Capacity + 1];
    private static readonly Dictionary<string, int> s_names = new(StringComparer.Ordinal);

    /// <summary>
    /// Reserves a slot for a parameter's hooks, or finds the slot of an identical earlier definition.
    /// </summary>
    /// <typeparam name="T">The parameter's managed value type.</typeparam>
    /// <param name="name">The parameter name.</param>
    /// <param name="kind">The native kind.</param>
    /// <param name="hooks">The hooks, with at least one present.</param>
    /// <param name="options">The enumerated labels, or null for other kinds.</param>
    /// <param name="created">Whether the slot was newly reserved and must be released if definition fails.</param>
    /// <returns>The slot number.</returns>
    /// <exception cref="PgException">The parameter already has different hooks.</exception>
    /// <exception cref="InvalidOperationException">Every slot is in use.</exception>
    internal static int Reserve<T>(string name, int kind, PgGucHooks<T> hooks, IReadOnlyList<PgGucEnumOption>? options, out bool created)
    {
        string key = Key(name);
        lock (s_gate)
        {
            if (s_names.TryGetValue(key, out int existing))
            {
                Entry entry = s_slots[existing]!;
                if (entry.Kind != kind || entry.Hooks is not PgGucHooks<T> earlier || !earlier.SameAs(hooks))
                {
                    throw new PgException(PgSqlStates.DuplicateObject,
                        $"configuration parameter \"{name}\" is already defined with different hooks");
                }

                created = false;
                return existing;
            }

            for (int slot = 1; slot <= Capacity; slot++)
            {
                if (s_slots[slot] is null)
                {
                    s_slots[slot] = new Entry(kind, hooks, options);
                    s_names.Add(key, slot);
                    created = true;
                    return slot;
                }
            }
        }

        throw new InvalidOperationException($"An extension can define at most {Capacity} run-time configuration parameters with hooks.");
    }

    /// <summary>
    /// Releases a slot whose definition failed.
    /// </summary>
    /// <param name="name">The parameter name.</param>
    /// <param name="slot">The reserved slot.</param>
    internal static void Release(string name, int slot)
    {
        lock (s_gate)
        {
            s_slots[slot] = null;
            s_names.Remove(Key(name));
        }
    }

    /// <summary>
    /// Gets the hooks registered for a slot.
    /// </summary>
    /// <param name="slot">The slot the native hook passed.</param>
    /// <returns>The registered hooks.</returns>
    internal static Entry Get(int slot)
    {
        lock (s_gate)
        {
            return slot is > 0 and <= Capacity && s_slots[slot] is { } entry
                ? entry
                : throw new InvalidOperationException("The native configuration hook named an unregistered run-time slot.");
        }
    }

    /// <summary>
    /// Folds ASCII letters, as PostgreSQL compares configuration names.
    /// </summary>
    /// <param name="name">The parameter name.</param>
    /// <returns>The comparison key.</returns>
    private static string Key(string name)
        => string.Create(name.Length, name, static (span, value) =>
        {
            for (int index = 0; index < value.Length; index++)
            {
                char current = value[index];
                span[index] = current is >= 'A' and <= 'Z' ? (char)(current + ('a' - 'A')) : current;
            }
        });

    /// <summary>
    /// One registered parameter's hooks.
    /// </summary>
    /// <param name="Kind">The native kind: 0 Boolean, 1 integer, 2 real, 3 string, 4 enumerated.</param>
    /// <param name="Hooks">The typed hooks.</param>
    /// <param name="Options">The enumerated labels, or null for other kinds.</param>
    internal sealed record Entry(int Kind, object Hooks, IReadOnlyList<PgGucEnumOption>? Options);
}
