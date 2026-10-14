namespace Ankus.Tool;

/// <summary>
/// Stages manifest replacements and rolls back completed writes when a later replacement fails.
/// </summary>
internal static class UpgradeTransaction
{
    /// <summary>
    /// Writes a fully resolved upgrade without leaving earlier manifests upgraded after an observed failure.
    /// </summary>
    /// <param name="documents">The original manifests and their planned edits.</param>
    /// <param name="token">Cancels preparation or a replacement, restoring already replaced files.</param>
    internal static async Task WriteAsync(IEnumerable<UpgradeDocument> documents, CancellationToken token)
    {
        UpgradeDocument[] snapshots = [.. documents];
        var staged = new List<(UpgradeDocument Document, byte[] Content, string Temporary, string Backup)>();
        int replaced = 0;
        try
        {
            foreach (UpgradeDocument document in snapshots.Where(static document => document.Changed))
            {
                token.ThrowIfCancellationRequested();
                if ((File.GetAttributes(document.Path) & FileAttributes.ReadOnly) != 0)
                {
                    throw new IOException($"Cannot upgrade the read-only manifest '{document.Path}'.");
                }

                byte[] content = document.RenderBytes();
                string temporary = document.Path + ".ankus-" + Guid.NewGuid().ToString("N");
                string backup = temporary + ".backup";
                staged.Add((document, content, temporary, backup));
                await File.WriteAllBytesAsync(temporary, content, token);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(temporary, File.GetUnixFileMode(document.Path));
                }
            }

            foreach (UpgradeDocument document in snapshots)
            {
                RequireUnchanged(document.Path, document.Original);
            }

            foreach ((UpgradeDocument document, _, string temporary, string backup) in staged)
            {
                token.ThrowIfCancellationRequested();
                RequireUnchanged(document.Path, document.Original);
                try
                {
                    File.Replace(temporary, document.Path, backup);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    throw new IOException($"Could not replace manifest '{document.Path}': {error.Message}", error);
                }

                replaced++;
            }
        }
        catch (Exception error)
        {
            var failures = new List<Exception> { error };
            for (int index = replaced - 1; index >= 0; index--)
            {
                (UpgradeDocument document, byte[] content, _, string backup) = staged[index];
                try
                {
                    RequireUnchanged(document.Path, content);
                    File.Move(backup, document.Path, overwrite: true);
                }
                // codeql[cs/catch-of-all-exceptions] Every file is restored even when another fails, and each failure is reported.
                catch (Exception rollback)
                {
                    failures.Add(new IOException($"Could not restore '{document.Path}'; its original file remains at '{backup}'.", rollback));
                }
            }

            if (failures.Count != 1)
            {
                throw new AggregateException("Framework upgrade failed and some original manifests require recovery.", failures);
            }

            throw;
        }
        finally
        {
            foreach ((_, _, string temporary, _) in staged)
            {
                File.Delete(temporary);
            }
        }

        foreach ((_, _, _, string backup) in staged)
        {
            File.Delete(backup);
        }
    }

    private static void RequireUnchanged(string path, byte[] expected)
    {
        if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(expected))
        {
            throw new IOException($"Manifest '{path}' changed during the upgrade. Retry using its current contents.");
        }
    }
}
