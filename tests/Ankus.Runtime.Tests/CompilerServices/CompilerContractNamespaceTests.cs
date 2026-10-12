using System.ComponentModel;
using System.Reflection;

namespace Ankus.Runtime.Tests.CompilerServices;

/// <summary>
/// Separates compiler-emitted transport contracts from the extension author's discoverable API.
/// </summary>
[TestClass]
public sealed class CompilerContractNamespaceTests
{
    /// <summary>
    /// Every public native transport helper is explicitly scoped and hidden while author APIs retain their namespace.
    /// </summary>
    [TestMethod]
    public void GeneratedCodeContractsAreSeparateFromAuthorApis()
    {
        Assembly runtime = typeof(PgDatum).Assembly;
        Type[] helpers = [.. runtime.GetExportedTypes().Where(static type => !type.IsNested &&
            type.Name.StartsWith("Native", StringComparison.Ordinal)).OrderBy(static type => type.Name, StringComparer.Ordinal)];
        string[] expected =
        [
            "NativeAggregate", "NativeBackend", "NativeCallArgument", "NativeCallError", "NativeCallbackContext", "NativeCustomScan",
            "NativeCustomScanRequest", "NativeError", "NativeEventTrigger", "NativeFunctionPointerAttribute", "NativeGuc", "NativeGucText", "NativeLog",
            "NativeMemoryContext", "NativeRawCall", "NativeRawCallback", "NativeRelationScope", "NativeSet", "NativeUnsafeAccessAttribute",
            "NativeValue",
        ];
        Assert.AreSequenceEqual(expected, helpers.Select(static type => type.Name));
        foreach (Type helper in helpers)
        {
            Assert.AreEqual("Ankus.CompilerServices", helper.Namespace, helper.Name);
            Assert.IsNull(runtime.GetType("Ankus." + helper.Name), "Compiler helpers must not remain in the author namespace.");
            EditorBrowsableAttribute visibility = Assert.IsInstanceOfType<EditorBrowsableAttribute>(helper.GetCustomAttribute<EditorBrowsableAttribute>());
            Assert.AreEqual(EditorBrowsableState.Never, visibility.State, helper.Name);
        }

        Type[] authorTypes =
        [
            typeof(PgDatum), typeof(PgNumeric), typeof(PgMemoryContext), typeof(PgTransaction), typeof(PgBackgroundWorker),
            typeof(PgFunctionAttribute), typeof(PgAggregateAttribute), typeof(Spi),
        ];
        foreach (Type author in authorTypes)
        {
            Assert.AreEqual("Ankus", author.Namespace, author.Name);
            Assert.AreSame(author, runtime.GetType("Ankus." + author.Name));
        }
    }
}
