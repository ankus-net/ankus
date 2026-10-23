namespace Ankus.Build.Tests;

/// <summary>
/// Verifies that helpers PostgreSQL turned from macros into functions, or back, are wrapped exactly where they are macros.
/// </summary>
[TestClass]
public sealed class NativeBindingHeaderTransitionsTests
{
    /// <summary>
    /// Every wrapped major lacks the function, another supported major declares it, and no major wraps a helper twice.
    /// </summary>
    [TestMethod]
    public void TransitionsWrapOnlyMajorsWithoutTheFunction()
    {
        Dictionary<int, NativeBindingRawCatalog> inventories = Enumerable.Range(13, 7)
            .ToDictionary(static major => major, static major => NativeBindingResources.ReadRawCatalog(major));
        Assert.HasCount(252, NativeBindingHeaderTransitions.All);
        Assert.HasCount(250, NativeBindingHeaderTransitions.All.Select(static transition => transition.Name).Distinct(StringComparer.Ordinal));
        foreach (NativeBindingHeaderTransition transition in NativeBindingHeaderTransitions.All)
        {
            Assert.IsGreaterThanOrEqualTo(13, transition.First, transition.Name);
            Assert.IsLessThanOrEqualTo(19, transition.Last, transition.Name);
            Assert.IsLessThanOrEqualTo(transition.Last, transition.First, transition.Name);
            int[] wrapped = [.. Enumerable.Range(transition.First, transition.Last - transition.First + 1)];
            Assert.DoesNotContain(major => inventories[major].Functions.ContainsKey(transition.Name), wrapped, transition.Name);
            Assert.Contains(major => inventories[major].Functions.ContainsKey(transition.Name), inventories.Keys.Except(wrapped), transition.Name);
        }

        foreach (int major in inventories.Keys)
        {
            IReadOnlyList<NativeBindingHeaderHelper> helpers = NativeBindingHeaderHelpers.Read(major);
            Assert.HasCount(helpers.Count, helpers.Select(static helper => helper.Name).Distinct(StringComparer.Ordinal));
            HashSet<string> requests = [.. NativeBindingHeaderHelpers.Requests(inventories[major]).Select(static request => request.Name)];
            foreach (NativeBindingHeaderTransition transition in NativeBindingHeaderTransitions.Read(major))
            {
                Assert.Contains(transition.Name, requests);
            }
        }
    }

    /// <summary>
    /// The tuple descriptor and attribute storage helpers are callable on every major, through wrappers where they are macros.
    /// </summary>
    /// <param name="name">The helper name.</param>
    /// <param name="function">The first major that declares the helper as a function.</param>
    [TestMethod]
    [DataRow("TupleDescAttr", 18)]
    [DataRow("fetch_att", 16)]
    [DataRow("att_isnull", 16)]
    [DataRow("store_att_byval", 16)]
    [DataRow("DatumGetInt32", 16)]
    [DataRow("VARSIZE", 19)]
    public void TupleHelpersAreCallableOnEveryMajor(string name, int function)
    {
        for (int major = 13; major <= 19; major++)
        {
            NativeBindingRawCatalog inventory = NativeBindingResources.ReadRawCatalog(major);
            bool wrapped = NativeBindingHeaderTransitions.Read(major).Any(transition => transition.Name == name);
            Assert.AreEqual(major < function, wrapped, $"{name} on {major}");
            Assert.AreEqual(major >= function, inventory.Functions.ContainsKey(name), $"{name} on {major}");
            NativeHeaderRequest[] requests = [.. NativeBindingHeaderHelpers.Requests(inventory).Where(request => request.Name == name)];
            Assert.AreSequenceEqual(wrapped ? ["ankus_header_" + name] : [], requests.Select(static request => request.NativeName), $"{name} on {major}");
        }
    }

    /// <summary>
    /// Wrapper bodies pass each parameter to the macro in order, including function pointers, and omit the result of void helpers.
    /// </summary>
    [TestMethod]
    public void TransitionBodiesCallTheMacroWithTheirParameters()
    {
        Assert.AreEqual("return TupleDescAttr(tupdesc, i);", Body("TupleDescAttr", 17));
        Assert.AreEqual("return fetch_att(T, attbyval, attlen);", Body("fetch_att", 15));
        Assert.AreEqual("store_att_byval(T, newdatum, attlen);", Body("store_att_byval", 15));
        Assert.AreEqual("return expression_tree_mutator(node, mutator, context);", Body("expression_tree_mutator", 16));
        Assert.AreEqual("WalSndWakeupProcessRequests();", Body("WalSndWakeupProcessRequests", 15));
        Assert.AreEqual("bool result; HeapKeyTest(tuple, tupdesc, nkeys, keys, result); return result;", Body("HeapKeyTest", 13));
        Assert.AreEqual("PageXLogRecPtrSet(*ptr, lsn);", Body("PageXLogRecPtrSet", 18));
        NativeBindingHeaderHelper helper = NativeBindingHeaderHelpers.Read(17).Single(static value => value.Name == "TupleDescAttr");
        Assert.AreEqual("ankus_header_TupleDescAttr", helper.NativeName);
        Assert.Contains("extern FormData_pg_attribute * ankus_header_TupleDescAttr(TupleDesc tupdesc, int i);", helper.Source!);
        Assert.Contains("#if defined(ANKUS_DEFINE_HEADER_TupleDescAttr)", helper.Source!);
        Assert.DoesNotContain(static value => value.Name == "TupleDescAttr", NativeBindingHeaderHelpers.Read(18));
    }

    private static string Body(string name, int major)
        => NativeBindingHeaderTransitions.Body(NativeBindingHeaderTransitions.Read(major).Single(transition => transition.Name == name));
}
