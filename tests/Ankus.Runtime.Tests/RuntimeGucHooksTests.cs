namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies how run-time configuration hooks are assigned to the native bridge's prebuilt hook slots.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RuntimeGucHooksTests
{
    /// <summary>
    /// Each hook present sets its native bit, so PostgreSQL installs only the hooks a definition supplies.
    /// </summary>
    [TestMethod]
    public void HookBitsFollowThePresentHooks()
    {
        Assert.AreEqual(0, new PgGucHooks<int>().Bits);
        Assert.AreEqual(1, new PgGucHooks<int> { Check = static (value, _) => new(value) }.Bits);
        Assert.AreEqual(2, new PgGucHooks<int> { Assign = static (_, _) => { } }.Bits);
        Assert.AreEqual(4, new PgGucHooks<int> { Show = static (value, _) => value.ToString(System.Globalization.CultureInfo.InvariantCulture) }.Bits);
        Assert.AreEqual(7, new PgGucHooks<string?>
        {
            Check = static (value, _) => new(value),
            Assign = static (_, _) => { },
            Show = static (value, _) => value ?? string.Empty,
        }.Bits);
    }

    /// <summary>
    /// A repeated definition with the same hook methods, as from a retried module load, reuses its slot under
    /// PostgreSQL's ASCII case folding; different hooks or another kind are rejected as a duplicate object.
    /// </summary>
    [TestMethod]
    public void RepeatedDefinitionsReuseTheirSlotOnlyWithTheSameHooks()
    {
        PgGucHooks<int> hooks = new() { Check = Even };
        int slot = RuntimeGucHooks.Reserve("unit_hooks.limit", 1, hooks, null, out bool created);
        try
        {
            Assert.IsTrue(created);
            Assert.AreEqual(slot, RuntimeGucHooks.Reserve("UNIT_HOOKS.Limit", 1, new PgGucHooks<int> { Check = Even }, null, out bool again));
            Assert.IsFalse(again);
            Assert.AreSame(hooks, RuntimeGucHooks.Get(slot).Hooks);

            PgException different = Assert.ThrowsExactly<PgException>(() =>
                RuntimeGucHooks.Reserve("unit_hooks.limit", 1, new PgGucHooks<int> { Check = static (value, _) => new(value) }, null, out _));
            Assert.AreEqual(PgSqlStates.DuplicateObject, different.SqlState);
            Assert.ThrowsExactly<PgException>(() => RuntimeGucHooks.Reserve("unit_hooks.limit", 4, hooks, [new("a", 0)], out _));
            Assert.ThrowsExactly<PgException>(() =>
                RuntimeGucHooks.Reserve("unit_hooks.limit", 0, new PgGucHooks<bool> { Assign = static (_, _) => { } }, null, out _));
        }
        finally
        {
            RuntimeGucHooks.Release("unit_hooks.limit", slot);
        }

        Assert.ThrowsExactly<InvalidOperationException>(() => RuntimeGucHooks.Get(slot));
    }

    /// <summary>
    /// The bridge has a fixed number of hook slots; one more definition with hooks is rejected without changing any
    /// registered slot, and released slots become available again.
    /// </summary>
    [TestMethod]
    public void SlotsAreLimitedAndReusable()
    {
        var reserved = new List<(string Name, int Slot)>();
        try
        {
            while (true)
            {
                string name = $"unit_capacity.p{reserved.Count}";
                try
                {
                    reserved.Add((name, RuntimeGucHooks.Reserve(name, 1, new PgGucHooks<int> { Check = Even }, null, out _)));
                }
                catch (InvalidOperationException)
                {
                    break;
                }
            }

            Assert.IsLessThanOrEqualTo(RuntimeGucHooks.Capacity, reserved.Count);
            Assert.IsTrue(reserved.All(static entry => entry.Slot is >= 1 and <= RuntimeGucHooks.Capacity));
            (string last, int slot) = reserved[^1];
            RuntimeGucHooks.Release(last, slot);
            reserved.RemoveAt(reserved.Count - 1);
            Assert.AreEqual(slot, RuntimeGucHooks.Reserve("unit_capacity.reused", 1, new PgGucHooks<int> { Check = Even }, null, out _));
            reserved.Add(("unit_capacity.reused", slot));
        }
        finally
        {
            foreach ((string name, int slot) in reserved)
            {
                RuntimeGucHooks.Release(name, slot);
            }
        }
    }

    /// <summary>
    /// Accepts even values.
    /// </summary>
    private static PgGucCheckResult<int> Even(int value, PgGucSource source)
        => value % 2 == 0 ? new(value) : new(new PgGucCheckError("odd"));
}
