namespace Ankus.TestExtension;

/// <summary>
/// Exercises the upstream ten-thousand-cell ownership case without letting checked borrowed cells revive after owner release.
/// </summary>
[PgSchema("array_lifetimes")]
public static class LargeArrayLifetimeFunctions
{
    /// <summary>
    /// Copies the first five strings before ending their native owner and checks each escaped borrowed alias afterward.
    /// </summary>
    /// <param name="input">The original caller-owned text array.</param>
    /// <param name="mode">Dispose the view, reset its source, delete its source or reset only the source.</param>
    /// <returns>Every expired-alias diagnostic, the five independent copies and the unchanged original first cell.</returns>
    [PgFunction]
    public static string?[] LargeArrayPrefixLifetime(PgArrayView<PgTextView?> input, int mode)
    {
        using PgMemoryContext owner = PgMemoryContext.Create("large array ownership");
        using var view = new PgArrayView<PgTextView?>(input.Datum.CopyTo(owner));
        PgTextView?[] aliases = [.. view.Take(5)];
        try
        {
            string?[] copies = [.. aliases.Select(static cell => cell?.ToString())];
            switch (mode)
            {
                case 0:
                    view.Dispose();
                    break;
                case 1:
                    owner.Reset();
                    break;
                case 2:
                    owner.Dispose();
                    break;
                case 3:
                    owner.ResetOnly();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode));
            }

            string?[] observations = new string?[aliases.Length];
            for (int index = 0; index < aliases.Length; index++)
            {
                try
                {
                    observations[index] = aliases[index]?.ToString();
                }
                catch (ObjectDisposedException)
                {
                    observations[index] = nameof(ObjectDisposedException);
                }
            }

            using PgTextView? first = input.Count == 0 ? null : input[0];
            return [.. observations, .. copies, first?.ToString()];
        }
        finally
        {
            Disposal.DisposeAll(aliases);
        }
    }
}
