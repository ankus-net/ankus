using System.Xml.Linq;

namespace Ankus.Tool;

/// <summary>
/// Identifies one framework version token and the surrounding XML value that must be preserved.
/// </summary>
/// <param name="Document">The source manifest.</param>
/// <param name="Node">The attribute or scalar element containing the version.</param>
/// <param name="Package">The exact framework package identity.</param>
/// <param name="Version">The version requirement or property expression.</param>
/// <param name="IsSdk">Whether this reference needs a concrete MSBuild SDK version.</param>
/// <param name="Prefix">Text preceding the version within the XML value.</param>
/// <param name="Suffix">Text following the version within the XML value.</param>
/// <param name="Item">An optional item that needs identity-specific metadata, leaving the shared version token unchanged.</param>
/// <param name="Condition">The condition restricting new metadata to its original condition and framework identity.</param>
internal sealed record UpgradeReference(UpgradeDocument Document, XObject Node, string Package, string Version,
    bool IsSdk, string Prefix = "", string Suffix = "", XElement? Item = null, string? Condition = null);
