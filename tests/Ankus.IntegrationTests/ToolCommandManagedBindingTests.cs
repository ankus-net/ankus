using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    private static readonly JsonSerializerOptions s_bindingJsonOptions = new() { MaxDepth = 512 };

    /// <summary>
    /// Binding discovery builds its companion without capturing native linker inputs before the extension is compiled.
    /// </summary>
    [TestMethod]
    public async Task SdkBindingDiscoveryLeavesNativeLinkInputsDeferred()
    {
        string root = CreateBindingDirectory();
        string project = Path.Join(root, "BindingDiscovery.csproj");
        File.Copy(s_project, project);
        ProcessResult result = await RunDotnetAsync(
            ["msbuild", project, "-restore", "-target:_ResolveAnkusBindings", "-verbosity:quiet",
                "-property:Configuration=Release", "-property:RuntimeIdentifier=" + RuntimeInformation.RuntimeIdentifier,
                "-property:AnkusPostgresMajor=" + MajorText(), "-property:AnkusPgConfigPath=" + s_installation.PgConfigPath,
                "-getItem:ManagedBinary,LinkerArg,NativeLibrary,_AnkusBindingAssembly",
                "-bl:" + Path.Join(root, "binding-discovery-{}.binlog")], context.CancellationToken);
        result.EnsureSuccess("dotnet", ["msbuild"]);
        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        JsonElement items = document.RootElement.GetProperty("Items");
        Assert.AreEqual(0, items.GetProperty("ManagedBinary").GetArrayLength());
        Assert.AreEqual(0, items.GetProperty("LinkerArg").GetArrayLength());
        Assert.AreEqual(0, items.GetProperty("NativeLibrary").GetArrayLength());
        JsonElement companion = Assert.ContainsSingle(items.GetProperty("_AnkusBindingAssembly").EnumerateArray());
        string? assembly = companion.GetProperty("FullPath").GetString();
        Assert.IsNotNull(assembly);
        Assert.IsTrue(File.Exists(assembly));
        Assert.StartsWith("Ankus.Postgres.Pg", Path.GetFileName(assembly));
    }

    /// <summary>
    /// Separate SDK projects exchange the same native types and retain them through Native AOT publication, rebuild and clean.
    /// </summary>
    [TestMethod]
    public async Task SdkSharesNativeTypesAcrossProjectsAndPublishesThem()
    {
        CancellationToken token = context.CancellationToken;
        string root = CreateBindingDirectory();
        string provider = Path.Join(root, "provider");
        string consumer = Path.Join(root, "consumer");
        Directory.CreateDirectory(provider);
        Directory.CreateDirectory(consumer);
        LinkBindingIntermediates(provider);
        LinkBindingIntermediates(consumer);
        string providerProject = Path.Join(provider, "BindingProvider.csproj");
        string consumerProject = Path.Join(consumer, "BindingConsumer.csproj");
        File.Copy(s_project, providerProject);
        XDocument project = XDocument.Load(s_project);
        project.Root!.Add(new XElement("ItemGroup", new XElement("ProjectReference",
            new XAttribute("Include", providerProject))));
        string nativeProvider = await CompileBindingProviderAsync(root, token);
        project.Root.Add(new XElement("ItemGroup", new XElement("NativeLibrary", new XAttribute("Include", nativeProvider))));
        if (s_installation.Version.Major < 16)
        {
            project.Root.Add(new XElement("ItemGroup", new XElement("DirectPInvoke",
                new XAttribute("Include", "BindingProvider.Native!_PG_output_plugin_init"))));
        }

        project.Save(consumerProject);
        await File.WriteAllTextAsync(Path.Join(provider, "BindingProvider.cs"), """
            using Ankus.Postgres;
            public static class BindingProvider
            {
                public static RangeTblRef Create(int value) => new() { type = NodeTag.T_RangeTblRef, rtindex = value };
                public static void Increment(ref RangeTblRef value) => value.rtindex++;
                public static ErrorData CreateError(int value) => new() { elevel = value, output_to_client = true };
            }
            """, token);
        await File.WriteAllTextAsync(Path.Join(consumer, "BindingConsumer.cs"), """
            using Ankus;
            using Ankus.Postgres;
            public static partial class BindingConsumer
            {
                [PgFunction]
                public static int NativeBindingRoundTrip(int input)
                {
                    RangeTblRef value = BindingProvider.Create(input);
                    BindingProvider.Increment(ref value);
                    return value.rtindex;
                }

                [PgFunction]
                public static bool NativeBindingTag() => BindingProvider.Create(9).type == NodeTag.T_RangeTblRef;

                [PgFunction]
                public static unsafe int NativeBindingSize() => sizeof(RangeTblRef);

                [PgFunction]
                public static string NativeBindingRid() => NativeBinding.RuntimeIdentifier;

                [PgFunction]
                public static int NativeDependencyRoundTrip(int input)
                {
                    ErrorData value = BindingProvider.CreateError(input);
                    return value.output_to_client ? value.elevel : -1;
                }

                [PgFunction]
                public static string NativeRecordResult()
                {
                    unsafe
                    {
                        FullTransactionId value = NativeMethods.FullTransactionIdFromU64(0xFEDCBA9876543210UL);
                        return value.value.ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
                    }
                }

                [PgFunction]
                public static int NativeGlobalPid()
                {
                    unsafe
                    {
                        return NativeGlobals.MyProcPid;
                    }
                }

                [PgFunction]
                public static unsafe string NativeIndirectValues()
                {
                    uint oid = Spi.ExecuteScalar<uint>("SELECT 'pg_catalog.int4div(integer,integer)'::regprocedure::oid");
                    using PgMemoryContext owner = PgMemoryContext.Create("native indirect calls");
                    using PgNativeBox<FmgrInfo> function = owner.AllocateZeroedBox<FmgrInfo>();
                    FmgrInfo* info = (FmgrInfo*)function.DangerousGetPointer();
                    NativeMethods.fmgr_info_cxt(oid, info, NativeMethods.GetMemoryChunkContext(info));
                    using PgNativeBox<PGFunction> saved = owner.CreateBox(function.Value.fn_addr);
                    PGFunction target = saved.Value;
                    using PgAllocation frame = owner.AllocateZeroed((nuint)sizeof(FunctionCallInfoBaseData) + 2U * (nuint)sizeof(NullableDatum));
                    FunctionCallInfoBaseData* data = (FunctionCallInfoBaseData*)frame.DangerousGetPointer();
                    *data = new() { flinfo = info, nargs = 2 };
                    ulong first = InvokeIntegerDivision(target, frame, 84, 2);
                    ulong signedResult = InvokeIntegerDivision(target, frame, -85, 2);
                    string state = "missing";
                    try
                    {
                        PgTransaction.RunInSubtransaction(() => InvokeIntegerDivision(target, frame, 1, 0));
                    }
                    catch (PgException error) when (error.SqlState == "22012")
                    {
                        state = error.SqlState;
                    }

                    ulong recovered = InvokeIntegerDivision(saved.Value, frame, 126, 3);
                    bool identity = saved.Value.DangerousGetAddress() == function.Value.fn_addr.DangerousGetAddress();
                    return $"{first}|{signedResult:X16}|{state}|{recovered}|{identity}|{Spi.ExecuteScalar<int>("SELECT 6 * 7")}";
                }

                private static unsafe ulong InvokeIntegerDivision(PGFunction target, PgAllocation frame, int left, int right)
                {
                    FunctionCallInfoBaseData* data = (FunctionCallInfoBaseData*)frame.DangerousGetPointer();
                    data->isnull = false;
                    System.Span<NullableDatum> arguments = FunctionCallInfoBaseData.Dangerous_args(data, 2);
                    arguments[0] = new() { value = unchecked((ulong)left) };
                    arguments[1] = new() { value = unchecked((ulong)right) };
                    ulong result = target.Invoke(data);
                    if (data->isnull)
                    {
                        throw new System.InvalidOperationException("Unexpected native SQL NULL.");
                    }

                    return result;
                }

                [PgFunction]
                public static unsafe string NativeGlobalValues()
                {
                    int original = NativeGlobals.extra_float_digits;
                    int* address = NativeGlobals.DangerousAddressOf_extra_float_digits();
                    int changed;
                    int sqlChanged;
                    int recovered;
                    bool context;
                    try
                    {
                        NativeGlobals.extra_float_digits = -3;
                        changed = *address;
                        sqlChanged = Spi.ExecuteScalar<int>("SELECT current_setting('extra_float_digits')::integer");
                        using PgMemoryContext owner = PgMemoryContext.Create("native globals");
                        using PgAllocation allocation = owner.AllocateZeroed(16);
                        MemoryContextData* nativeOwner = NativeMethods.GetMemoryChunkContext(allocation.DangerousGetPointer());
                        MemoryContextData* previous = NativeGlobals.CurrentMemoryContext;
                        NativeGlobals.CurrentMemoryContext = nativeOwner;
                        try
                        {
                            context = PgMemoryContext.Current.Id == owner.Id &&
                                *NativeGlobals.DangerousAddressOf_CurrentMemoryContext() == nativeOwner;
                        }
                        finally
                        {
                            NativeGlobals.CurrentMemoryContext = previous;
                        }
                        try
                        {
                            PgTransaction.RunInSubtransaction(() => Spi.Execute("SELECT 1 / 0"));
                            throw new System.InvalidOperationException("Expected PostgreSQL division error.");
                        }
                        catch (PgException error) when (error.SqlState == "22012")
                        {
                            recovered = NativeGlobals.extra_float_digits;
                        }
                    }
                    finally
                    {
                        NativeGlobals.extra_float_digits = original;
                    }
                    return $"{original}|{changed}|{sqlChanged}|{recovered}|{NativeGlobals.extra_float_digits}|{context}|{address == NativeGlobals.DangerousAddressOf_extra_float_digits()}";
                }

                [PgFunction]
                public static unsafe string NativeGlobalLinkage()
                {
                    int timeout = NativeGlobals.CheckPointTimeout;
                    int* address = NativeGlobals.DangerousAddressOf_CheckPointTimeout();
                    NativeGlobals.CheckPointTimeout = timeout + 1;
                    int changed = *address;
                    NativeGlobals.CheckPointTimeout = timeout;
            #if ANKUS_PG13 || ANKUS_PG14
                    string status = System.OperatingSystem.IsWindows() ? "unexported"
                        : NativeGlobals.wal_receiver_status_interval.ToString(System.Globalization.CultureInfo.InvariantCulture);
            #else
                    string status = NativeGlobals.wal_receiver_status_interval.ToString(System.Globalization.CultureInfo.InvariantCulture);
            #endif
                    return $"{timeout}|{changed - timeout}|{NativeGlobals.CheckPointTimeout}|{status}";
                }

                [PgFunction]
                public static unsafe string NativeGuardedInline()
                {
                    nint buffer = (nint)NativeMethods.makeStringInfo();
                    ((StringInfoData*)buffer)->len = 0x3FFFFFFE;
                    try
                    {
                        PgTransaction.RunInSubtransaction(() =>
                        {
                            unsafe
                            {
                                NativeMethods.pq_sendint32((StringInfoData*)buffer, 7);
                            }
                        });
                        return "sent";
                    }
                    catch (PgException error)
                    {
                        return error.SqlState + "|" + Spi.ExecuteScalar<int>("SELECT 6 * 7");
                    }
                }

                [PgFunction]
                public static unsafe bool NativeLinkedProvider()
                {
                    OutputPluginCallbacks callbacks = new() { startup_cb = new((void*)17), shutdown_cb = new((void*)23) };
            #if ANKUS_PG13 || ANKUS_PG14 || ANKUS_PG15
                    InitializeLinkedProvider(&callbacks);
            #else
                    NativeMethods._PG_output_plugin_init(&callbacks);
            #endif
                    return callbacks.startup_cb.IsNull && callbacks.shutdown_cb.DangerousGetAddress() == (void*)23;
                }

            #if ANKUS_PG13 || ANKUS_PG14 || ANKUS_PG15
                /// <summary>
                /// Calls the fixture-owned initializer, which only updates callback storage and cannot raise PostgreSQL ERROR.
                /// </summary>
                [System.Runtime.InteropServices.LibraryImport("BindingProvider.Native", EntryPoint = "_PG_output_plugin_init")]
                [System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
                private static unsafe partial void InitializeLinkedProvider(OutputPluginCallbacks* callbacks);
            #endif

                [PgFunction]
                public static unsafe int NativeNodeCall(int input)
                {
            #if ANKUS_PG13 || ANKUS_PG14
                    Value* address = NativeMethods.makeInteger(input);
            #else
                    Integer* address = NativeMethods.makeInteger(input);
            #endif
                    try
                    {
            #if ANKUS_PG13 || ANKUS_PG14
                        Value value = *address;
            #else
                        Integer value = *address;
            #endif
                        if (value.type != NodeTag.T_Integer)
                        {
                            throw new System.InvalidOperationException("Unexpected native node tag.");
                        }

            #if ANKUS_PG13 || ANKUS_PG14
                        return value.val.ival;
            #else
                        return value.ival;
            #endif
                    }
                    finally
                    {
                        NativeMethods.pfree(address);
                    }
                }

                [PgFunction]
                public static unsafe string NativeHeaderHelperValues()
                {
                    using PgMemoryContext owner = PgMemoryContext.Create("header helpers");
                    using PgAllocation page = owner.AllocateZeroed(8192);
                    sbyte* address = (sbyte*)page.DangerousGetPointer();
                    MemoryContextData* nativeOwner = NativeMethods.GetMemoryChunkContext(address);
                    bool ownership = nativeOwner != null && NativeMethods.MemoryContextIsValid(nativeOwner) && !NativeMethods.MemoryContextIsValid(null);
                    nint previousId = PgMemoryContext.Current.Id;
                    MemoryContextData* previous = NativeMethods.MemoryContextSwitchTo(nativeOwner);
                    bool selected;
                    bool restored;
                    try
                    {
                        selected = PgMemoryContext.Current.Id == owner.Id;
                    }
                    finally
                    {
                        restored = NativeMethods.MemoryContextSwitchTo(previous) == nativeOwner;
                    }

                    restored &= PgMemoryContext.Current.Id == previousId;
                    NativeMethods.PageInit(address, 8192, 16);
                    NativeMethods.PageSetPageSizeAndVersion(address, 8192, 199);
                    NativeMethods.PageValidateSpecialPointer(address);
                    bool pointers = NativeMethods.PageGetContents(address) == address + (nint)NativeMethods.MAXALIGN(NativeMethods.SizeOfPageHeaderData()) &&
                        NativeMethods.PageGetSpecialPointer(address) == address + 8176;
                    bool pageState = NativeMethods.PageIsValid(address) && !NativeMethods.PageIsValid(null) &&
                        NativeMethods.PageIsEmpty(address) && !NativeMethods.PageIsNew(address) &&
                        NativeMethods.PageGetMaxOffsetNumber(address) == 0 && NativeMethods.PageGetPageSize(address) == 8192 &&
                        NativeMethods.PageSizeIsValid(8192) && !NativeMethods.PageSizeIsValid(8191) && !NativeMethods.PageSizeIsValid(8193);
                    ItemIdData* item = NativeMethods.PageGetItemId(address, 1);
                    item->lp_off = 512;
                    item->lp_flags = 1;
                    item->lp_len = 3;
                    ((PageHeaderData*)address)->pd_lower = checked((ushort)(NativeMethods.SizeOfPageHeaderData() + (ulong)sizeof(ItemIdData)));
                    bool itemState = NativeMethods.ItemIdGetOffset(item) == 512 &&
                        NativeMethods.PageGetItem(address, item) == address + 512 && NativeMethods.PageGetMaxOffsetNumber(address) == 1;
                    byte version = NativeMethods.PageGetPageLayoutVersion(address);
                    ushort special = NativeMethods.PageGetSpecialSize(address);
                    bool ids = !NativeMethods.TransactionIdIsNormal(2) && NativeMethods.TransactionIdIsNormal(3) &&
                        NativeMethods.TransactionIdPrecedes(uint.MaxValue, 3) && NativeMethods.TransactionIdFollows(3, uint.MaxValue) &&
                        NativeMethods.TransactionIdPrecedesOrEquals(3, 3) && NativeMethods.TransactionIdFollowsOrEquals(3, 3);
                    bool categories = NativeMethods.BufferIsLocal(-1) && !NativeMethods.BufferIsLocal(0) &&
                        NativeMethods.type_is_array(1007) && !NativeMethods.type_is_array(23);
                    using PgAllocation name = owner.AllocateUtf8String("value");
                    TupleDescData* descriptor = NativeMethods.CreateTemplateTupleDesc(1);
                    ulong value;
                    bool tupleState;
                    bool nullState;
                    bool recovered = false;
                    try
                    {
                        NativeMethods.TupleDescInitEntry(descriptor, 1, (sbyte*)name.DangerousGetPointer(), 23, -1, 0);
            #if ANKUS_PG19
                        NativeMethods.TupleDescFinalize(descriptor);
            #endif
                        ulong datum = unchecked((ulong)-42L);
                        bool isNull = false;
                        HeapTupleData* tuple = NativeMethods.heap_form_tuple(descriptor, &datum, &isNull);
                        try
                        {
                            HeapTupleHeaderData* headerAddress = tuple->t_data;
                            value = NativeMethods.heap_getattr(tuple, 1, descriptor, &isNull);
                            tupleState = !isNull && NativeMethods.HeapTupleNoNulls(tuple) &&
                                NativeMethods.HeapTupleHeaderGetNatts(headerAddress) == 1 &&
                                NativeMethods.GETSTRUCT(tuple) == (sbyte*)headerAddress + headerAddress->t_hoff;
                            bool* nullAddress = &isNull;
                            try
                            {
                                PgTransaction.RunInSubtransaction(() => NativeMethods.heap_getattr(tuple, 0, descriptor, nullAddress));
                            }
                            catch (PgException error)
                            {
                                recovered = error.SqlState == "XX000" && error.Message == "invalid attnum: 0" &&
                                    NativeMethods.heap_getattr(tuple, 1, descriptor, nullAddress) == datum && !isNull;
                            }
                        }
                        finally
                        {
                            NativeMethods.heap_freetuple(tuple);
                        }

                        isNull = true;
                        tuple = NativeMethods.heap_form_tuple(descriptor, &datum, &isNull);
                        try
                        {
                            isNull = false;
                            ulong nullDatum = NativeMethods.heap_getattr(tuple, 1, descriptor, &isNull);
                            nullState = isNull && nullDatum == 0 && !NativeMethods.HeapTupleNoNulls(tuple);
                        }
                        finally
                        {
                            NativeMethods.heap_freetuple(tuple);
                        }
                    }
                    finally
                    {
                        NativeMethods.FreeTupleDesc(descriptor);
                    }

                    ulong aligned = NativeMethods.TYPEALIGN(8, 9);
                    ulong high = NativeMethods.MAXALIGN(0xFEDCBA9876543211UL);
                    owner.Reset();
                    bool expired = false;
                    try
                    {
                        page.DangerousGetPointer();
                    }
                    catch (System.ObjectDisposedException)
                    {
                        expired = true;
                    }

                    return $"{aligned}|{high:X16}|{version}|{special}|{value:X16}|{ownership}|{selected}|{restored}|{pointers}|{pageState}|{itemState}|{ids}|{categories}|{tupleState}|{nullState}|{recovered}|{expired}";
                }

                [PgFunction]
                public static unsafe string NativeVersionedHelperValues()
                {
                    // Older majors define these helpers as macros, so the same calls must work there through wrappers.
                    using PgMemoryContext owner = PgMemoryContext.Create("versioned helpers");
                    using PgAllocation name = owner.AllocateUtf8String("value");
                    TupleDescData* descriptor = NativeMethods.CreateTemplateTupleDesc(2);
                    string attributes;
                    try
                    {
                        NativeMethods.TupleDescInitEntry(descriptor, 1, (sbyte*)name.DangerousGetPointer(), 23, -1, 0);
                        NativeMethods.TupleDescInitEntry(descriptor, 2, (sbyte*)name.DangerousGetPointer(), 25, -1, 0);
            #if ANKUS_PG19
                        NativeMethods.TupleDescFinalize(descriptor);
            #endif
                        FormData_pg_attribute* first = NativeMethods.TupleDescAttr(descriptor, 0);
                        FormData_pg_attribute* second = NativeMethods.TupleDescAttr(descriptor, 1);
                        attributes = $"{first->atttypid}:{first->attlen}:{first->attbyval}:{second->atttypid}:{second->attlen}:{second->attbyval}";
                    }
                    finally
                    {
                        NativeMethods.FreeTupleDesc(descriptor);
                    }

                    int value = -42;
                    int fetched = NativeMethods.DatumGetInt32(NativeMethods.fetch_att(&value, true, 4));
                    long stored = 0;
                    NativeMethods.store_att_byval(&stored, NativeMethods.Int32GetDatum(1234), 4);
                    byte bits = 0b101;
                    string nulls = $"{NativeMethods.att_isnull(0, &bits)}:{NativeMethods.att_isnull(1, &bits)}:{NativeMethods.att_isnull(2, &bits)}";
                    using PgAllocation varlena = owner.AllocateZeroed(16);
                    void* text = varlena.DangerousGetPointer();
                    NativeMethods.SET_VARSIZE(text, 9);
                    bool data = NativeMethods.VARSIZE(text) == 9 && NativeMethods.VARDATA(text) == (sbyte*)text + 4;
                    ItemPointerData item;
                    NativeMethods.ItemPointerSet(&item, 70000, 12);
                    bool pointer = NativeMethods.DatumGetPointer(NativeMethods.PointerGetDatum(text)) == (sbyte*)text;
                    return $"{attributes}|{fetched}|{*(int*)&stored}|{nulls}|{data}|" +
                        $"{NativeMethods.ItemPointerGetBlockNumber(&item)}:{NativeMethods.ItemPointerGetOffsetNumber(&item)}|{pointer}|{NativeMethods.BITMAPLEN(9)}";
                }

                [PgFunction]
                public static string NativeTypedError(uint functionOid, bool swallow)
                {
                    unsafe
                    {
                        PgMemoryContext owner = PgMemoryContext.Current;
                        PgMemoryContext? failedContext = null;
                        PgException failure;
                        try
                        {
                            PgTransaction.RunInSubtransaction(() =>
                            {
                                failedContext = PgMemoryContext.Current;
                                if (swallow)
                                {
                                    Spi.Connect(session =>
                                    {
                                        session.Execute("INSERT INTO native_typed_effects VALUES (1)");
                                        try { NativeMethods.OidFunctionCall1Coll(functionOid, 0, 0); }
                                        catch (PgException) { }
                                    });
                                }
                                else
                                {
                                    Spi.Execute("INSERT INTO native_typed_effects VALUES (1)");
                                    NativeMethods.OidFunctionCall1Coll(functionOid, 0, 0);
                                }
                            });
                            throw new System.InvalidOperationException("The deliberate native error did not occur.");
                        }
                        catch (PgException exception) { failure = exception; }
                        bool restored = owner.Id == PgMemoryContext.Current.Id;
                        bool expired = failedContext is not null && !failedContext.IsAlive;
                        bool nestedEntered = false;
                        ulong recovered = Spi.Connect(session =>
                        {
                            ulong value = PgTransaction.RunInSubtransaction(() =>
                            {
                                Spi.Execute("INSERT INTO native_typed_effects VALUES (2)");
                                try
                                {
                                    PgTransaction.RunInSubtransaction(() =>
                                    {
                                        Spi.Execute("INSERT INTO native_typed_effects VALUES (3)");
                                        nestedEntered = true;
                                        NativeMethods.OidFunctionCall1Coll(functionOid, 0, 0);
                                    });
                                }
                                catch (PgException) { }
                                return NativeMethods.OidFunctionCall1Coll(functionOid, 0, 25);
                            });
                            if (session.ExecuteScalar<int>("SELECT sum(value)::integer FROM native_typed_effects") != 2)
                            {
                                throw new System.InvalidOperationException("The enclosing SPI session did not recover.");
                            }
                            return value;
                        });
                        return $"{failure.SqlState}|{failure.Message}|{failure.Detail}|{failure.Hint}|{restored}|{expired}|{nestedEntered}|{recovered}";
                    }
                }

                [PgFunction]
                public static bool NativeManagedRecovery()
                {
                    var original = new System.InvalidOperationException("managed recovery");
                    try
                    {
                        PgTransaction.RunInSubtransaction(() =>
                        {
                            Spi.Execute("INSERT INTO native_typed_effects VALUES (5)");
                            throw original;
                        });
                    }
                    catch (System.InvalidOperationException error) when (object.ReferenceEquals(original, error))
                    {
                        PgMemoryContext owner = PgMemoryContext.Current;
                        PgMemoryContext success = PgTransaction.RunInSubtransaction(() => PgMemoryContext.Current);
                        return success.Id != owner.Id && success.IsAlive &&
                            Spi.ExecuteScalar<int>("SELECT sum(value)::integer FROM native_typed_effects") == 2;
                    }
                    return false;
                }

                [PgFunction]
                public static void NativeForbiddenRecovery()
                {
                    _ = PgTransaction.RegisterCallback(PgTransactionEvent.PreCommit, () =>
                    {
                        try
                        {
                            PgTransaction.RunInSubtransaction(() =>
                                Spi.Execute("INSERT INTO native_typed_effects VALUES (6)"));
                        }
                        catch (PgException) { }
                    });
                }
            }
            """, token);
        string[] options = ["-c", "Release", "-r", RuntimeInformation.RuntimeIdentifier,
            "-p:AnkusPostgresMajor=" + MajorText(), "-p:AnkusPgConfigPath=" + s_installation.PgConfigPath];
        await File.WriteAllTextAsync(Path.Join(provider, "BindingCallbacks.cs"), ManagedNativeCallbackSource, token);
        await File.WriteAllTextAsync(Path.Join(provider, "BindingHookCallbacks.cs"), NativeHookSource, token);
        await File.WriteAllTextAsync(Path.Join(consumer, "CallbackExports.cs"), NativeCallbackExportSource, token);
        string unsafeProbe = Path.Join(consumer, "MissingUnsafe.cs");
        await File.WriteAllTextAsync(unsafeProbe, """
            using Ankus.Postgres;
            public static class MissingUnsafe
            {
                public static int Read() => NativeGlobals.MyProcPid;
                public static void Write() => NativeGlobals.extra_float_digits = 1;
                public static ulong Call() => NativeMethods.TYPEALIGN(8, 13);
                public static void Invoke(NativeGlobals_shmem_startup_hookCallback callback) => callback.Invoke();

                public static int Unexported()
                {
                    unsafe
                    {
                        return NativeGlobals.wal_receiver_status_interval;
                    }
                }
            }
            """, token);
        ProcessResult rejected = await RunDotnetAsync(["build", consumerProject, .. options], token);
        Assert.AreNotEqual(0, rejected.ExitCode);
        string diagnostics = rejected.StandardOutput + rejected.StandardError;
        string[] operations = ["MyProcPid", "extra_float_digits", "TYPEALIGN", "Invoke"];
        foreach (string operation in operations)
        {
            Assert.Contains("error ANKUS129: Use an unsafe block for '" + operation + "'", diagnostics);
        }

        // PostgreSQL 13 and 14 leave this variable out of postgres.exe's exports, so on Windows the platform analyzer
        // reports an unguarded read instead of the linker failing on it (#116).
        if (OperatingSystem.IsWindows() && s_installation.Version.Major < 15)
        {
            Assert.Contains("error CA1416: This call site is reachable on all platforms. 'NativeGlobals.wal_receiver_status_interval' " +
                $"is unsupported on: 'windows' (PostgreSQL {s_installation.Version.Major} does not export wal_receiver_status_interval from postgres.exe", diagnostics);
        }
        else
        {
            Assert.DoesNotContain("CA1416", diagnostics);
        }

        File.Delete(unsafeProbe);
        string published = Path.Join(root, "published");
        // The headers show which helpers cannot raise an error, and only those run without the guard (#115).
        string companion = await File.ReadAllTextAsync(Directory.GetFiles(Path.Join(consumer, "obj"), "native-binding.g.cs",
            SearchOption.AllDirectories).First(), token);
        foreach (string helper in new[] { "GETSTRUCT", "MemoryContextSwitchTo", "BufferIsLocal", "TupleDescAttr" })
        {
            Assert.AreEqual("InvokeDirect", RawCallEntry(companion, helper), helper);
        }

        foreach (string function in new[] { "pq_sendint32", "type_is_array", "PageInit" })
        {
            Assert.AreEqual("Invoke", RawCallEntry(companion, function), function);
        }

        // Go to Definition opens a declaration's own small document from the companion's PDB instead of decompiling a
        // class of several hundred thousand lines (#130).
        string companionAssembly = (await File.ReadAllTextAsync(Directory.GetFiles(Path.Join(consumer, "obj"), "native-binding.assembly-path",
            SearchOption.AllDirectories).First(), token)).Trim();
        using (var symbols = new CompanionSymbols(companionAssembly))
        {
            const string Documents = "/_/Ankus.Postgres/documents/";
            string method = symbols.MemberDocument("NativeMethods", "TupleDescAttr");
            Assert.AreEqual(Documents + "NativeMethods.TupleDescAttr.g.cs", method);
            Assert.Contains(" @TupleDescAttr(", symbols.Documents[method]!);
            Assert.DoesNotContain(" @pq_sendint32(", symbols.Documents[method]!);
            Assert.AreEqual(Documents + "NativeGlobals.MyProcPid.g.cs", symbols.MemberDocument("NativeGlobals", "get_MyProcPid"));
            Assert.AreSequenceEqual<string>([Documents + "NodeTag.g.cs"], symbols.TypeDocuments("NodeTag"));
            KeyValuePair<string, string?>[] generated = [.. symbols.Documents.Where(static pair => pair.Key.StartsWith(Documents, StringComparison.Ordinal))];
            Assert.IsGreaterThan(1000, generated.Length);
            Assert.IsTrue(generated.All(static pair => pair.Value is not null), "Every generated document is embedded.");
            Assert.IsLessThan(256 * 1024, generated.Max(static pair => pair.Value!.Length));
        }

        (await RunDotnetAsync(["publish", consumerProject, .. options, "-o", published], token))
            .EnsureSuccess("dotnet", ["publish"]);

        string providerAssembly = Assert.ContainsSingle(Directory.GetFiles(Path.Join(provider, "bin"), "Ankus.Postgres.*.dll", SearchOption.AllDirectories));
        string consumerAssembly = Assert.ContainsSingle(Directory.GetFiles(Path.Join(consumer, "bin"), "Ankus.Postgres.*.dll", SearchOption.AllDirectories));
        Assert.AreEqual(Path.GetFileName(providerAssembly), Path.GetFileName(consumerAssembly));
        byte[] expected = SHA256.HashData(await File.ReadAllBytesAsync(providerAssembly, token));
        Assert.AreSequenceEqual(expected, SHA256.HashData(await File.ReadAllBytesAsync(consumerAssembly, token)));
        Assert.IsEmpty(Directory.GetFiles(published, "Ankus.Postgres.*.dll"));
        DateTime written = File.GetLastWriteTimeUtc(consumerAssembly);
        ProcessResult repeated = await RunDotnetAsync(["build", consumerProject, .. options, "--no-restore"], token);
        repeated.EnsureSuccess("dotnet", ["build"]);
        Assert.Contains("Native binding sources: reused after native verification.", repeated.StandardOutput);
        Assert.AreEqual(written, File.GetLastWriteTimeUtc(consumerAssembly));

        string driver = Path.Join(root, "driver");
        Directory.CreateDirectory(driver);
        string driverProject = Path.Join(driver, "BindingDriver.csproj");
        new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup", new XElement("TargetFramework", "net10.0"),
                new XElement("OutputType", "Exe"), new XElement("TreatWarningsAsErrors", "true")),
            new XElement("ItemGroup", new XElement("ProjectReference", new XAttribute("Include", consumerProject)))))
            .Save(driverProject);
        await File.WriteAllTextAsync(Path.Join(driver, "Program.cs"), """
            Ankus.Postgres.RangeTblRef value = BindingProvider.Create(41);
            System.Console.WriteLine($"{value.rtindex}|{BindingConsumer.NativeBindingRoundTrip(value.rtindex)}");
            """, token);
        ProcessResult managed = await RunDotnetAsync(["run", "--project", driverProject, .. options], token);
        managed.EnsureSuccess("dotnet", ["run"]);
        Assert.EndsWith("41|42", managed.StandardOutput.Trim());
        await File.WriteAllTextAsync(Path.Join(driver, "Program.cs"), """
            Ankus.Postgres.RangeTblRef value = BindingProvider.Create(51);
            System.Console.WriteLine($"{value.rtindex}|{BindingConsumer.NativeBindingRoundTrip(value.rtindex)}");
            """, token);
        (await RunDotnetAsync(["build", driverProject, .. options, "--no-restore", "-p:BuildProjectReferences=false"], token))
            .EnsureSuccess("dotnet", ["build"]);
        string driverAssembly = Path.Join(driver, "bin", "Release", "net10.0", RuntimeInformation.RuntimeIdentifier, "BindingDriver.dll");
        ProcessResult existing = await RunDotnetAsync([driverAssembly], token);
        existing.EnsureSuccess("dotnet", [driverAssembly]);
        Assert.AreEqual("51|52", existing.StandardOutput.Trim());

        await using (PostgresTestCluster cluster = await StartPublishedClusterAsync(published, token))
        {
            await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
            await using var command = new NpgsqlCommand("CREATE EXTENSION ankus_tool_probe; SELECT native_binding_round_trip(9)", connection);
            Assert.AreEqual(10, await command.ExecuteScalarAsync(token));
            command.CommandText = "SELECT native_binding_tag()";
            Assert.IsTrue(Assert.IsInstanceOfType<bool>(await command.ExecuteScalarAsync(token)));
            command.CommandText = "SELECT native_binding_size()";
            Assert.AreEqual(8, await command.ExecuteScalarAsync(token));
            command.CommandText = "SELECT native_binding_rid()";
            Assert.AreEqual(RuntimeInformation.RuntimeIdentifier, await command.ExecuteScalarAsync(token));
            command.CommandText = "SELECT native_dependency_round_trip(37)";
            Assert.AreEqual(37, await command.ExecuteScalarAsync(token));
            command.CommandText = "SELECT native_record_result()";
            Assert.AreEqual("FEDCBA9876543210", await command.ExecuteScalarAsync(token));
            command.CommandText = "SELECT native_global_pid() = pg_backend_pid()";
            Assert.IsTrue(Assert.IsInstanceOfType<bool>(await command.ExecuteScalarAsync(token)));
            command.CommandText = "SELECT native_indirect_values()";
            Assert.AreEqual("42|FFFFFFFFFFFFFFD6|22012|42|True|42", await command.ExecuteScalarAsync(token));
            Assert.AreEqual("42|FFFFFFFFFFFFFFD6|22012|42|True|42", await command.ExecuteScalarAsync(token));
            command.CommandText = "SELECT native_managed_callback_values()";
            const string Callbacks = "EEDCBA9876543210|FFDCBA9876543210|True|True|True|22023|managed callback café|owned callback detail|retry callback|22012|division by zero|EEDCBA9876543210|4|42";
            Assert.AreEqual(Callbacks, await command.ExecuteScalarAsync(token));
            Assert.AreEqual(Callbacks, await command.ExecuteScalarAsync(token));
            await AssertNativeCallbackWorkersAsync(connection, null, token);
            await AssertNativeHookChainingAsync(command, token);
            command.CommandText = "SET extra_float_digits = 2; SELECT native_global_values()";
            Assert.AreEqual("2|-3|-3|-3|2|True|True", await command.ExecuteScalarAsync(token));
            command.CommandText = "SELECT current_setting('extra_float_digits')::integer";
            Assert.AreEqual(2, await command.ExecuteScalarAsync(token));
            command.CommandText = "SELECT setting FROM pg_settings WHERE name = 'checkpoint_timeout'";
            string timeout = Assert.IsInstanceOfType<string>(await command.ExecuteScalarAsync(token));
            command.CommandText = "SELECT setting FROM pg_settings WHERE name = 'wal_receiver_status_interval'";
            string status = OperatingSystem.IsWindows() && s_installation.Version.Major < 15 ? "unexported"
                : Assert.IsInstanceOfType<string>(await command.ExecuteScalarAsync(token));
            command.CommandText = "SELECT native_global_linkage()";
            Assert.AreEqual($"{timeout}|1|{timeout}|{status}", await command.ExecuteScalarAsync(token));
            command.CommandText = "SELECT native_linked_provider()";
            Assert.IsTrue(Assert.IsInstanceOfType<bool>(await command.ExecuteScalarAsync(token)));
            // An inline helper that can enlarge its buffer keeps the guard, so PostgreSQL's ERROR arrives as an exception
            // and the backend stays usable (#115).
            command.CommandText = "SELECT native_guarded_inline()";
            Assert.AreEqual("54000|42", await command.ExecuteScalarAsync(token));
            command.CommandText = "SELECT native_node_call(-2147483648)";
            Assert.AreEqual(int.MinValue, await command.ExecuteScalarAsync(token));
            command.CommandText = "SELECT native_node_call(2147483647)";
            Assert.AreEqual(int.MaxValue, await command.ExecuteScalarAsync(token));
            command.CommandText = "SELECT native_node_call(0)";
            Assert.AreEqual(0, await command.ExecuteScalarAsync(token));
            command.CommandText = "SELECT native_header_helper_values()";
            Assert.AreEqual("16|FEDCBA9876543218|199|16|FFFFFFFFFFFFFFD6|True|True|True|True|True|True|True|True|True|True|True|True",
                await command.ExecuteScalarAsync(token));
            command.CommandText = "SELECT native_versioned_helper_values()";
            Assert.AreEqual("23:4:True:25:-1:False|-42|1234|False:True:False|True|70000:12|True|2", await command.ExecuteScalarAsync(token));
            command.CommandText = "SELECT native_node_call(42)";
            Assert.AreEqual(42, await command.ExecuteScalarAsync(token));
            command.CommandText = "SELECT set_config('dynamic_library_path', current_setting('dynamic_library_path') || $1, false)";
            command.Parameters.AddWithValue(Path.PathSeparator + "$libdir");
            await command.ExecuteNonQueryAsync(token);
            command.Parameters.Clear();
            var notices = new List<PostgresNotice>();
            connection.Notice += (_, arguments) => notices.Add(arguments.Notice);
            command.CommandText = """
                CREATE TEMP TABLE native_typed_effects(value integer);
                CREATE FUNCTION native_typed_target(value integer) RETURNS integer LANGUAGE plpgsql AS $$
                BEGIN
                    IF value = 0 THEN
                        INSERT INTO native_typed_effects VALUES (4);
                        RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'typed native failure',
                            DETAIL = 'owned typed detail', HINT = 'retry typed call';
                    END IF;
                    RETURN value + 17;
                END;
                $$;
                SELECT native_typed_error('native_typed_target(integer)'::regprocedure::oid, false)
                """;
            Assert.AreEqual("22023|typed native failure|owned typed detail|retry typed call|True|True|True|42", await command.ExecuteScalarAsync(token));
            command.CommandText = "SELECT array_agg(value) FROM native_typed_effects";
            Assert.AreSequenceEqual([2], Assert.IsInstanceOfType<int[]>(await command.ExecuteScalarAsync(token)));
            command.CommandText = "TRUNCATE native_typed_effects; SELECT native_typed_error('native_typed_target(integer)'::regprocedure::oid, true)";
            Assert.AreEqual("22023|typed native failure|owned typed detail|retry typed call|True|True|True|42", await command.ExecuteScalarAsync(token));
            command.CommandText = "SELECT array_agg(value) FROM native_typed_effects";
            Assert.AreSequenceEqual([2], Assert.IsInstanceOfType<int[]>(await command.ExecuteScalarAsync(token)));
            command.CommandText = "SELECT native_managed_recovery()";
            Assert.IsTrue(Assert.IsInstanceOfType<bool>(await command.ExecuteScalarAsync(token)));
            command.CommandText = "SELECT native_forbidden_recovery()";
            PostgresException forbidden = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteNonQueryAsync(token));
            Assert.AreEqual("55000", forbidden.SqlState);
            Assert.AreEqual("Explicit recovery scopes are unavailable during transaction callbacks", forbidden.MessageText);
            command.CommandText = "SELECT array_agg(value) FROM native_typed_effects";
            Assert.AreSequenceEqual([2], Assert.IsInstanceOfType<int[]>(await command.ExecuteScalarAsync(token)));
            Assert.IsEmpty(notices, "Native error recovery must not leave PostgreSQL's SPI stack unbalanced.");
            command.CommandText = "SELECT native_node_call(42)";
            Assert.AreEqual(42, await command.ExecuteScalarAsync(token));
            await AssertNativeModuleLoadRetryAsync(cluster, token);
        }

        await AssertNativeCallbackPreloadAsync(published, token);

        (await RunDotnetAsync(["clean", consumerProject, .. options], token)).EnsureSuccess("dotnet", ["clean"]);
        Assert.IsFalse(File.Exists(providerAssembly));
        Assert.IsFalse(File.Exists(consumerAssembly));
        Assert.IsEmpty(Directory.GetFiles(root, "native-binding.g.cs", SearchOption.AllDirectories));
        Assert.IsEmpty(Directory.GetFiles(root, "Ankus.NativeBindings.csproj", SearchOption.AllDirectories));
        (await RunDotnetAsync(["build", consumerProject, .. options], token)).EnsureSuccess("dotnet", ["build"]);
        Assert.AreSequenceEqual(expected, SHA256.HashData(await File.ReadAllBytesAsync(consumerAssembly, token)));

        string artifacts = Path.Join(root, "isolated artifacts");
        (await RunDotnetAsync(["build", consumerProject, .. options, "--artifacts-path", artifacts], token))
            .EnsureSuccess("dotnet", ["build"]);
        string[] references = Directory.GetFiles(artifacts, "native-binding.assembly-path", SearchOption.AllDirectories);
        Assert.HasCount(2, references);
        foreach (string reference in references)
        {
            string assembly = (await File.ReadAllTextAsync(reference, token)).Trim();
            Assert.StartsWith(Path.GetDirectoryName(reference)! + Path.DirectorySeparatorChar, Path.GetFullPath(assembly));
            Assert.AreSequenceEqual(expected, SHA256.HashData(await File.ReadAllBytesAsync(assembly, token)));
        }
    }

    private static async Task<string> CompileBindingProviderAsync(string root, CancellationToken token)
    {
        string directory = Directory.CreateDirectory(Path.Join(root, "native provider")).FullName;
        string file = Path.Join(directory, "provider.c");
        string artifact = Path.Join(directory, OperatingSystem.IsWindows() ? "provider.obj" : "provider.o");
        string library = Path.Join(directory, OperatingSystem.IsWindows() ? "provider.lib" : "libprovider.a");
        await File.WriteAllTextAsync(file, """
            #include "postgres.h"
            #include "replication/output_plugin.h"
            PGDLLEXPORT void _PG_output_plugin_init(OutputPluginCallbacks *callbacks)
            {
                callbacks->startup_cb = NULL;
            }
            """, token);
        string compiler = OperatingSystem.IsWindows() ? "cl.exe" : "clang";
        string[] options = OperatingSystem.IsWindows()
            ? ["/nologo", "/std:c11", "/WX", "/O2", "/c", "/I" + s_installation.ServerIncludeDirectory, "/I" + s_installation.IncludeDirectory,
                "/I" + Path.Join(s_installation.ServerIncludeDirectory, "port", "win32"),
                "/I" + Path.Join(s_installation.ServerIncludeDirectory, "port", "win32_msvc"), "/Fo" + artifact, file]
            : [.. await s_installation.GetPreprocessorArgumentsAsync(token), "-std=gnu11", "-Wall", "-Wextra", "-Werror", "-O2", "-fPIC", "-c",
                "-isystem", s_installation.ServerIncludeDirectory, "-isystem", s_installation.IncludeDirectory, file, "-o", artifact];
        await PackageProcessRunner.RunCheckedAsync(compiler, options, s_environment, token, workingDirectory: directory);
        string archiver = OperatingSystem.IsWindows() ? "lib.exe" : "ar";
        string[] archive = OperatingSystem.IsWindows() ? ["/nologo", "/OUT:" + library, artifact] : ["rcs", library, artifact];
        await PackageProcessRunner.RunCheckedAsync(archiver, archive, s_environment, token, workingDirectory: directory);
        return library;
    }

    /// <summary>
    /// A compiler targeting a different runtime cannot emit a managed binding contract for the requested runtime.
    /// </summary>
    [TestMethod]
    public async Task PackagedBuildToolRejectsMismatchedBindingRuntime()
    {
        string helper = await ReadPackagedBuildToolAsync();
        string output = Path.Join(s_root, "mismatched binding runtime");
        string otherRuntime = RuntimeInformation.RuntimeIdentifier == "linux-x64" ? "linux-arm64" : "linux-x64";
        ProcessResult result = await RunDotnetAsync(
            [helper, "binding-sources", MajorText(), s_installation.PgConfigPath, output, "", "", otherRuntime],
            context.CancellationToken);
        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains($"Native bindings target {RuntimeInformation.RuntimeIdentifier}", result.StandardError);
        Assert.Contains($"requested runtime is {otherRuntime}", result.StandardError);
        Assert.IsFalse(File.Exists(Path.Join(output, "native-binding.g.cs")));
        Assert.IsFalse(File.Exists(Path.Join(output, "Ankus.NativeBindings.csproj")));
    }

    /// <summary>
    /// Long output paths retain a complete companion, clean temporary ASTs after success and failure, and permit deterministic recovery.
    /// </summary>
    [TestMethod]
    public async Task PackagedNodeBindingFailurePreservesCompanionAndRecovers()
    {
        CancellationToken token = context.CancellationToken;
        string helper = await ReadPackagedBuildToolAsync();
        string root = CreateBindingDirectory();
        // Reproduce a valid output path whose former nested compiler working directory exceeded Windows MAX_PATH.
        string output = Path.Join(root, new string('p', Math.Max(1, 220 - root.Length - 1)));
        string temporary = Directory.CreateTempSubdirectory("ankus-node-test-").FullName;
        try
        {
            await VerifyNodeBindingRecoveryAsync(helper, output, temporary, token);
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    private static async Task VerifyNodeBindingRecoveryAsync(string helper, string output, string temporary, CancellationToken token)
    {
        var environment = new Dictionary<string, string?>(s_environment)
        {
            ["TMPDIR"] = temporary,
            ["TMP"] = temporary,
            ["TEMP"] = temporary,
        };
        string sentinel = Path.Join(temporary, "unrelated.txt");
        await File.WriteAllTextAsync(sentinel, "preserve unrelated temporary files", token);
        string cache = Path.Join(temporary, "binding-cache");
        string[] command = [helper, "binding-sources", MajorText(), s_installation.PgConfigPath, output, "", "", "", "", "", "", cache];
        (await PackageProcessRunner.RunAsync("dotnet", command, environment, token, workingDirectory: s_root)).EnsureSuccess("dotnet", command);
        Assert.IsEmpty(Directory.GetDirectories(temporary, "ankus-node-*"));
        Assert.IsEmpty(Directory.GetDirectories(temporary, "ankus-source-*"));
        string[] names = ["native-binding.g.cs", "native-binding.assembly-name", "native-binding.identity", "Ankus.NativeBindings.csproj",
            "native-records.json", "native-availability.json", "native-layout.c", "native-layout.txt", "native-layout.json", "native-node-availability.json",
            "native-node-declarations.json"];
        var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string name in names)
        {
            expected.Add(name, await File.ReadAllBytesAsync(Path.Join(output, name), token));
        }

        ProcessResult rejected = await PackageProcessRunner.RunAsync("dotnet",
            [.. command[..^2], Path.Join(output, "missing-libclang"), cache], environment, token, workingDirectory: s_root);
        Assert.AreEqual(1, rejected.ExitCode);
        Assert.Contains("Cannot locate the selected libclang library", rejected.StandardError);
        foreach (string name in names)
        {
            Assert.AreSequenceEqual(expected[name], await File.ReadAllBytesAsync(Path.Join(output, name), token), name);
        }

        Assert.IsEmpty(Directory.GetDirectories(temporary, "ankus-node-*"));
        Assert.IsEmpty(Directory.GetDirectories(temporary, "ankus-source-*"));
        JsonNode records = JsonNode.Parse(expected["native-records.json"], documentOptions: new()
        {
            MaxDepth = 512
        })!;
        JsonNode range = records["Graph"]!["Declarations"]!.AsArray().Single(static declaration => declaration!["Name"]!.GetValue<string>() == "RangeTblRef")!;
        JsonNode field = range["Fields"]!.AsArray().Single(static member => member!["Name"]!.GetValue<string>() == "rtindex")!;
        Assert.AreEqual(32, field["OffsetBits"]!.GetValue<int>());
        field["OffsetBits"] = 0;
        // Keep the cache manifest internally consistent so only native verification can reject this false layout.
        await ReplaceCachedArtifactAsync(cache, "native-records.json", JsonSerializer.SerializeToUtf8Bytes(records, s_bindingJsonOptions), token);
        ProcessResult invalidLayout = await PackageProcessRunner.RunAsync("dotnet", command, environment, token, workingDirectory: s_root);
        Assert.AreEqual(1, invalidLayout.ExitCode);
        Assert.Contains("offset RangeTblRef.rtindex", invalidLayout.StandardError);
        foreach (string name in names)
        {
            Assert.AreSequenceEqual(expected[name], await File.ReadAllBytesAsync(Path.Join(output, name), token), name);
        }

        Assert.IsEmpty(Directory.GetDirectories(temporary, "ankus-node-*"));
        Assert.IsEmpty(Directory.GetDirectories(temporary, "ankus-source-*"));
        await ReplaceCachedArtifactAsync(cache, "native-records.json", expected["native-records.json"], token);
        await ReplaceCachedArtifactAsync(cache, "native-node-availability.json", "[{\"Type\":\"RangeTblRef\",\"Field\":\"rtindex\"}]"u8.ToArray(), token);
        ProcessResult invalidAvailability = await PackageProcessRunner.RunAsync("dotnet", command, environment, token, workingDirectory: s_root);
        Assert.AreEqual(1, invalidAvailability.ExitCode);
        Assert.Contains("Current native node observations disagree with the cached companion contract", invalidAvailability.StandardError);
        foreach (string name in names)
        {
            Assert.AreSequenceEqual(expected[name], await File.ReadAllBytesAsync(Path.Join(output, name), token), name);
        }

        Assert.IsEmpty(Directory.GetDirectories(temporary, "ankus-node-*"));
        Assert.IsEmpty(Directory.GetDirectories(temporary, "ankus-source-*"));
        await ReplaceCachedArtifactAsync(cache, "native-node-availability.json", expected["native-node-availability.json"], token);
        await ReplaceCachedArtifactAsync(cache, "native-node-declarations.json",
            "{\"AbsentTypes\":[\"RangeTblRef\"],\"AbsentTags\":[],\"AdditionalTags\":[],\"ChangedTags\":[]}"u8.ToArray(), token);
        ProcessResult invalidDeclarations = await PackageProcessRunner.RunAsync("dotnet", command, environment, token, workingDirectory: s_root);
        Assert.AreEqual(1, invalidDeclarations.ExitCode);
        Assert.Contains("Current native node observations disagree with the cached companion contract", invalidDeclarations.StandardError);
        foreach (string name in names)
        {
            Assert.AreSequenceEqual(expected[name], await File.ReadAllBytesAsync(Path.Join(output, name), token), name);
        }

        Assert.IsEmpty(Directory.GetDirectories(temporary, "ankus-node-*"));
        Assert.IsEmpty(Directory.GetDirectories(temporary, "ankus-source-*"));
        await ReplaceCachedArtifactAsync(cache, "native-node-declarations.json", expected["native-node-declarations.json"], token);
        string concurrentOutput = Path.Join(temporary, "concurrent consumer");
        string[] concurrentCommand = [.. command];
        concurrentCommand[4] = concurrentOutput;
        ProcessResult[] recovered = await Task.WhenAll(
            PackageProcessRunner.RunAsync("dotnet", command, environment, token, workingDirectory: s_root),
            PackageProcessRunner.RunAsync("dotnet", concurrentCommand, environment, token, workingDirectory: s_root));
        foreach (ProcessResult result in recovered)
        {
            result.EnsureSuccess("dotnet", command);
            Assert.Contains("Native binding sources: reused after native verification.", result.StandardOutput);
        }

        foreach (string name in names)
        {
            Assert.AreSequenceEqual(expected[name], await File.ReadAllBytesAsync(Path.Join(output, name), token), name);
            if (name != "Ankus.NativeBindings.csproj")
            {
                Assert.AreSequenceEqual(expected[name], await File.ReadAllBytesAsync(Path.Join(concurrentOutput, name), token), name);
            }
        }

        Assert.IsEmpty(Directory.GetDirectories(temporary, "ankus-node-*"));
        Assert.IsEmpty(Directory.GetDirectories(temporary, "ankus-source-*"));
        Assert.AreEqual("preserve unrelated temporary files", await File.ReadAllTextAsync(sentinel, token));
    }

    /// <summary>
    /// Replaces only this test's cached observation and its content hash to exercise independent native verification.
    /// </summary>
    /// <param name="cache">The isolated cache owned by this test.</param>
    /// <param name="name">The observation artifact to replace.</param>
    /// <param name="content">The complete replacement observation.</param>
    /// <param name="token">Cancels fixture file access.</param>
    private static async Task ReplaceCachedArtifactAsync(string cache, string name, byte[] content, CancellationToken token)
    {
        string file = Assert.ContainsSingle(Directory.GetFiles(cache, name, SearchOption.AllDirectories));
        await File.WriteAllBytesAsync(file, content, token);
        string manifestPath = Path.Join(Path.GetDirectoryName(file)!, "manifest.json");
        JsonNode manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, token))!;
        JsonNode artifact = manifest["Artifacts"]!.AsArray().Single(entry => entry!["Path"]!.GetValue<string>() == name)!;
        artifact["Hash"] = Convert.ToHexString(SHA256.HashData(content));
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(), token);
    }

    private string CreateBindingDirectory()
        => PhysicalBindingDirectory(new DirectoryInfo(CreateDirectory()));

    private static string PhysicalBindingDirectory(DirectoryInfo directory)
    {
        if (directory.Parent is null)
        {
            return directory.FullName;
        }

        DirectoryInfo resolved = (DirectoryInfo?)directory.ResolveLinkTarget(returnFinalTarget: true) ?? directory;
        return resolved.Parent is DirectoryInfo parent
            ? Path.Join(PhysicalBindingDirectory(parent), resolved.Name)
            : resolved.FullName;
    }

    private static void LinkBindingIntermediates(string project)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string intermediate = Path.Join(project, "obj", "Release", "net10.0", RuntimeInformation.RuntimeIdentifier);
        string physical = Path.Join(intermediate, "physical bindings");
        Directory.CreateDirectory(physical);
        string linked = Path.Join(intermediate, "ankus-bindings");
        Directory.CreateSymbolicLink(linked, physical);
    }

    /// <summary>
    /// Names the raw call entry that a generated method's body calls.
    /// </summary>
    /// <param name="companion">The generated binding source.</param>
    /// <param name="method">The generated method's name.</param>
    /// <returns>The <c>NativeRawCall</c> method the body calls.</returns>
    private static string RawCallEntry(string companion, string method)
    {
        const string Prefix = "global::Ankus.CompilerServices.NativeRawCall.";
        int declaration = companion.IndexOf(" @" + method + "(", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, declaration, method);
        int call = companion.IndexOf(Prefix, declaration, StringComparison.Ordinal) + Prefix.Length;
        return companion[call..companion.IndexOf('(', call)];
    }
}
