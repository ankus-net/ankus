namespace Ankus;

/// <summary>
/// Supplies the choices that generators turn into values, recording them so a failing input can be replayed and shrunk.
/// </summary>
/// <remarks>
/// Every value is built from a sequence of unsigned choices in which zero is the simplest. A random source draws
/// fresh choices; a replay source reads a recorded sequence and supplies zero once it runs out. Shrinking edits the
/// sequence and replays it, so values built with <see cref="PgGenerator{T}.Select{TResult}"/> and the other
/// combinators shrink without type-specific code, as in Hypothesis.
/// </remarks>
internal sealed class PgPropertySource
{
    /// <summary>
    /// The most choices one value may use, which bounds generators that never stop drawing.
    /// </summary>
    internal const int MaxChoices = 8_192;

    /// <summary>
    /// SplitMix64's increment, the 64-bit golden ratio.
    /// </summary>
    private const ulong Golden = 0x9E37_79B9_7F4A_7C15;

    private readonly List<ulong> _choices = [];
    private readonly ulong[]? _replay;
    private ulong _state;

    private PgPropertySource(ulong seed, ulong[]? replay)
    {
        _state = seed;
        _replay = replay;
    }

    /// <summary>
    /// Gets the choices drawn so far.
    /// </summary>
    internal IReadOnlyList<ulong> Choices => _choices;

    /// <summary>
    /// Creates a source that draws random choices from a seed.
    /// </summary>
    /// <param name="seed">The seed.</param>
    /// <returns>The source.</returns>
    internal static PgPropertySource Random(ulong seed) => new(seed, null);

    /// <summary>
    /// Creates a source that replays recorded choices.
    /// </summary>
    /// <param name="choices">The recorded choices.</param>
    /// <returns>The source.</returns>
    internal static PgPropertySource Replay(ulong[] choices) => new(0, choices);

    /// <summary>
    /// Gets whether the source replays recorded choices; otherwise it draws random ones.
    /// </summary>
    internal bool Replaying => _replay is not null;

    /// <summary>
    /// Draws a choice from zero through <paramref name="maximum"/>, uniformly when random.
    /// </summary>
    /// <param name="maximum">The largest choice.</param>
    /// <returns>The choice.</returns>
    internal ulong Choose(ulong maximum) => Record(maximum, _replay is null ? Uniform(maximum) : 0);

    /// <summary>
    /// Records a choice the caller drew from its own distribution, or replays the recorded one.
    /// </summary>
    /// <param name="maximum">The largest choice.</param>
    /// <param name="generated">The choice to record when random; ignored when replaying.</param>
    /// <returns>The choice.</returns>
    internal ulong Choose(ulong maximum, ulong generated) => Record(maximum, Math.Min(generated, maximum));

    /// <summary>
    /// Draws uniformly from zero through <paramref name="maximum"/> without recording a choice.
    /// </summary>
    /// <param name="maximum">The largest value.</param>
    /// <returns>The value, or zero when replaying.</returns>
    internal ulong Draw(ulong maximum) => _replay is null ? Uniform(maximum) : 0;

    /// <summary>
    /// Draws a choice that is usually zero: when random, one draw in <paramref name="oneIn"/> is uniform over one
    /// through <paramref name="maximum"/>.
    /// </summary>
    /// <param name="maximum">The largest choice.</param>
    /// <param name="oneIn">How rarely a nonzero choice is drawn.</param>
    /// <returns>The choice.</returns>
    internal ulong ChooseRarely(ulong maximum, uint oneIn)
        => Record(maximum, _replay is null && maximum != 0 && Uniform(oneIn - 1) == 0 ? 1 + Uniform(maximum - 1) : 0);

    /// <summary>
    /// Draws a choice that is usually nonzero: when random, it is zero once in <paramref name="oneIn"/> draws.
    /// </summary>
    /// <param name="oneIn">How rarely zero is drawn.</param>
    /// <returns>Zero or one.</returns>
    internal bool Continue(uint oneIn) => Record(1, _replay is null && Uniform(oneIn - 1) != 0 ? 1UL : 0) != 0;

    private ulong Record(ulong maximum, ulong random)
    {
        if (_choices.Count == MaxChoices)
        {
            throw new PgPropertyOverrunException();
        }

        ulong choice = _replay is null ? random : _choices.Count < _replay.Length ? Math.Min(_replay[_choices.Count], maximum) : 0;
        _choices.Add(choice);
        return choice;
    }

    /// <summary>
    /// Draws uniformly from zero through <paramref name="maximum"/> by Lemire's multiply-shift method.
    /// </summary>
    private ulong Uniform(ulong maximum)
    {
        if (maximum == ulong.MaxValue)
        {
            return Next();
        }

        ulong range = maximum + 1;
        ulong threshold = (0 - range) % range;
        while (true)
        {
            UInt128 product = (UInt128)Next() * range;
            if ((ulong)product >= threshold)
            {
                return (ulong)(product >> 64);
            }
        }
    }

    /// <summary>
    /// Derives the seed of one case from a run's seed.
    /// </summary>
    /// <param name="seed">The run's seed.</param>
    /// <param name="attempt">The zero-based case attempt, counting rejected cases.</param>
    /// <returns>The case's seed.</returns>
    internal static ulong CaseSeed(ulong seed, int attempt) => Mix(seed + ((ulong)attempt * Golden));

    /// <summary>
    /// Advances SplitMix64, which is fixed here so a reported seed reproduces its inputs on every runtime.
    /// </summary>
    private ulong Next() => Mix(_state += Golden);

    /// <summary>
    /// SplitMix64's output function.
    /// </summary>
    private static ulong Mix(ulong value)
    {
        value = (value ^ (value >> 30)) * 0xBF58_476D_1CE4_E5B9;
        value = (value ^ (value >> 27)) * 0x94D0_49BB_1331_11EB;
        return value ^ (value >> 31);
    }
}

/// <summary>
/// Reports that a value drew more choices than one input may use.
/// </summary>
internal sealed class PgPropertyOverrunException() : Exception("A generator drew more than " + PgPropertySource.MaxChoices + " choices for one value.");
