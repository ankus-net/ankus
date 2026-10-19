using System.Text;

namespace Ankus;

/// <summary>
/// Shares strict Unicode encoding between both serialization formats.
/// </summary>
internal static class PgSerializationText
{
    /// <summary>
    /// Rejects unpaired UTF-16 surrogates so stored data is never silently replaced.
    /// </summary>
    internal static UTF8Encoding Utf8 { get; } = new(false, true);
}
