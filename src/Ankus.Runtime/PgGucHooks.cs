namespace Ankus;

/// <summary>
/// Supplies check, assign and show hooks for a configuration parameter defined at run time with
/// <see cref="PgGucRegistry"/>, as pgrx's <c>define_*_guc_with_hooks</c> functions accept hook functions.
/// </summary>
/// <typeparam name="T">
/// The parameter's value type: <see cref="bool"/>, <see cref="int"/>, <see cref="double"/>, a nullable
/// <see cref="string"/>, or the <see cref="int"/> value of an enumerated parameter's label.
/// </typeparam>
/// <remarks>
/// <para>
/// The hooks follow the rules for the hooks a <see cref="PgGucAttribute"/> names: a check can normalize or reject a
/// proposed value and attach <see cref="PgGucExtra"/>, assign runs before PostgreSQL changes the value and must not
/// fail, and show returns display text. Leave a hook null to omit it.
/// </para>
/// <para>
/// An extension can define at most 64 parameters with hooks. Defining a parameter again, as a retried module load
/// does, must pass the same hook methods.
/// </para>
/// </remarks>
public sealed class PgGucHooks<T>
{
    /// <summary>
    /// Gets the check that accepts, normalizes or rejects a proposed value.
    /// </summary>
    public Func<T, PgGucSource, PgGucCheckResult<T>>? Check
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the hook that runs before PostgreSQL assigns an accepted value, with its extra data.
    /// </summary>
    public Action<T, PgGucExtra?>? Assign
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the hook that formats the current value, with its extra data, for display.
    /// </summary>
    public Func<T, PgGucExtra?, string>? Show
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the native hook bits: 1 for check, 2 for assign and 4 for show.
    /// </summary>
    internal int Bits => (Check is null ? 0 : 1) | (Assign is null ? 0 : 2) | (Show is null ? 0 : 4);

    /// <summary>
    /// Compares the hook methods, so a repeated definition can be recognized.
    /// </summary>
    /// <param name="other">The hooks of an earlier definition.</param>
    /// <returns>Whether every hook is the same method on the same target.</returns>
    internal bool SameAs(PgGucHooks<T> other)
        => Equals(Check, other.Check) && Equals(Assign, other.Assign) && Equals(Show, other.Show);
}
