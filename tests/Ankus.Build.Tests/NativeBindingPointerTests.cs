using Microsoft.CodeAnalysis;

namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// Pointer arrays, multiple indirection, void and opaque pointees preserve native identity, layout and exact address bits.
    /// </summary>
    [TestMethod]
    public async Task ManagedPointerStoragePreservesNativeLayout()
    {
        const string Headers = """
            #include <stdint.h>
            #include <stddef.h>
            typedef struct Opaque Opaque;
            typedef struct Links {
                int *first;
                int **nested;
                void *untyped;
                Opaque *opaque;
                int *items[3];
                int *tail[];
            } Links;
            extern Links observed;
            """;
        const string Main = """
            #include <stdio.h>
            #include <stdlib.h>
            int main(void) {
                Links *value = calloc(1, sizeof(Links) + 3 * sizeof(int*));
                value->first = (int*)(uintptr_t)0x1234;
                value->nested = (int**)(uintptr_t)0x5678;
                value->untyped = (void*)(uintptr_t)0x9ABC;
                value->opaque = (Opaque*)(uintptr_t)0xDEF0;
                value->items[0] = (int*)(uintptr_t)0x1020;
                value->items[2] = (int*)(uintptr_t)0x3040;
                value->tail[0] = value->items[2];
                value->tail[2] = value->items[0];
                printf("%zu %zu %zu %zu %zu %zu %zu %zu %zu %zu %zu %zu\n",
                    sizeof(Links), offsetof(Links, items), offsetof(Links, tail), sizeof(value->items[0]),
                    (size_t)value->first, (size_t)value->nested, (size_t)value->untyped, (size_t)value->opaque,
                    (size_t)value->items[0], (size_t)value->items[1], (size_t)value->tail[0], (size_t)value->tail[2]);
                free(value);
                return 0;
            }
            """;
        const string Harness = """
            using System;
            using Ankus.Postgres;
            public static class BindingAssertions
            {
                public static unsafe long[] Run()
                {
                    byte* storage = stackalloc byte[sizeof(Links) + 3 * sizeof(int*)];
                    new Span<byte>(storage, sizeof(Links) + 3 * sizeof(int*)).Clear();
                    Links* value = (Links*)storage;
                    value->first = (int*)0x1234;
                    value->nested = (int**)0x5678;
                    value->untyped = (void*)0x9ABC;
                    value->opaque = (Opaque*)0xDEF0;
                    value->items[0] = (int*)0x1020;
                    value->items[2] = (int*)0x3040;
                    Span<NativePointer0> tail = Links.Dangerous_tail(value, 3);
                    tail[0] = value->items[2];
                    tail[2] = value->items[0];
                    if (!tail[1].IsNull || tail.Length != 3 || !Links.Dangerous_tail(value, 0).IsEmpty)
                    {
                        throw new InvalidOperationException("Pointer container shape or null value changed.");
                    }

                    try
                    {
                        _ = tail[3];
                        throw new InvalidOperationException("Upper bound accepted.");
                    }
                    catch (IndexOutOfRangeException)
                    {
                    }

                    try
                    {
                        _ = tail[-1];
                        throw new InvalidOperationException("Lower bound accepted.");
                    }
                    catch (IndexOutOfRangeException)
                    {
                    }

                    try
                    {
                        _ = Links.Dangerous_tail(value, -1);
                        throw new InvalidOperationException("Negative extent accepted.");
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                    }

                    try
                    {
                        _ = Links.Dangerous_tail(null, 1);
                        throw new InvalidOperationException("Null owner accepted.");
                    }
                    catch (ArgumentNullException)
                    {
                    }

                    int* restored = tail[0];
                    return [sizeof(Links), (byte*)&value->items - storage, (byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref tail[0]) - storage,
                        System.Runtime.CompilerServices.Unsafe.ByteOffset(ref value->items[0], ref value->items[1]),
                        (nint)value->first, (nint)value->nested, (nint)value->untyped, (nint)value->opaque,
                        (nint)value->items[0].DangerousGetAddress(), (nint)value->items[1].DangerousGetAddress(),
                        (nint)restored, (nint)tail[2].DangerousGetAddress()];
                }
            }
            """;
        string directory = Path.Join(Path.GetTempPath(), "ankus-pointer-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            NativeRecordGraph graph = await CollectRecordsAsync(Headers, [new("observed", "observed", false)], directory);
            NativeBindingSource binding = NativeBindingRecordCSharp.Generate(graph);
            long[] expected = await RunRecordWitnessAsync(Headers + "\n" + Main, directory);
            Assert.AreSequenceEqual(expected, GeneratedBindingCompilation.Run(binding, Harness, context.CancellationToken));
            Assert.AreEqual(binding, NativeBindingRecordCSharp.Generate(graph));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// The compiler rejects untyped integers, wrong pointees and pointer access without an unsafe context.
    /// </summary>
    /// <param name="declaration">The invalid consumer declaration.</param>
    /// <param name="diagnostic">The compiler error for that violated contract.</param>
    [TestMethod]
    [DataRow("public static nint Read(Links value) => value.first;", "CS0214")]
    [DataRow("public static unsafe void Write(ref Links value, nint address) => value.first = address;", "CS0266")]
    [DataRow("public static unsafe void Write(ref Links value, byte* address) => value.first = address;", "CS0266")]
    [DataRow("public static unsafe void Write(ref Links value, nint address) => value.items[0] = address;", "CS0029")]
    [DataRow("public static unsafe void Write(ref Links value, byte* address) => value.items[0] = address;", "CS0029")]
    [DataRow("public static unsafe void Write(ref Links value, nint address) => value.callback = new Arithmetic(address);", "CS1503")]
    [DataRow("public static void Address(Links value) => _ = value.callback.DangerousGetAddress();", "CS0214")]
    [DataRow("public static int Layout() => NativeSize<Opaque>(); private static int NativeSize<T>() where T : unmanaged, Ankus.IPgNativeType => T.NativeSize;", "CS0315")]
    public async Task ManagedPointerContractsRejectImplicitUnsafeAccess(string declaration, string diagnostic)
    {
        const string Headers = """
            typedef int (*Arithmetic)(int);
            typedef struct Opaque Opaque;
            typedef struct Links { int *first; int *items[2]; Arithmetic callback; Opaque *opaque; } Links;
            extern Links observed;
            """;
        string directory = Path.Join(Path.GetTempPath(), "ankus-pointer-compiler-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            NativeRecordGraph graph = await CollectRecordsAsync(Headers, [new("observed", "observed", false)], directory);
            NativeBindingSource binding = NativeBindingRecordCSharp.Generate(graph);
            Diagnostic[] errors = [.. GeneratedBindingCompilation.Diagnostics(binding,
                "using Ankus.Postgres; public static class Consumer { " + declaration + " }", context.CancellationToken)
                .Where(static item => item.Severity == DiagnosticSeverity.Error)];
            Assert.IsNotEmpty(errors);
            Assert.IsTrue(errors.All(static error => error.Location.SourceTree?.FilePath == "Consumer.cs"), string.Join("\n", errors.Select(static error => error.ToString())));
            Assert.Contains(diagnostic, errors.Select(static error => error.Id));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Typed addresses survive fixed and indirect calls and global reads, writes and address-of operations.
    /// </summary>
    [TestMethod]
    public async Task ManagedPointerCallsPreserveNativeValues()
    {
        const string Headers = """
            typedef int* (*Step)(int*);
            int cells[3] = { 17, 29, 41 };
            int *current = cells;
            int *ankus_pointer_advance(int *value) { return value + 1; }
            Step select_step(void) { return ankus_pointer_advance; }
            int **get_current(void) { return &current; }
            """;
        const string Harness = """
            public static class BindingAssertions
            {
                public static unsafe long[] Run()
                {
                    using NativeCallTestBridge.Scope scope = new();
                    int* first = NativeGlobals.current;
                    int* second = NativeMethods.ankus_pointer_advance(first);
                    Step advance = NativeMethods.select_step();
                    int* third = advance.Invoke(second);
                    int** current = NativeMethods.get_current();
                    bool same = current == NativeGlobals.DangerousAddressOf_current();
                    NativeGlobals.current = third;
                    return [*first, *second, *third, *NativeGlobals.current, **current,
                        second - first, third - second, same ? 1 : 0];
                }
            }
            """;
        Assert.AreSequenceEqual<long>([17, 29, 41, 41, 41, 1, 1, 1],
            await ExecuteManagedCallsAsync(Headers, ["ankus_pointer_advance", "select_step", "get_current"], Harness, globalNames: ["current"]));
    }

    /// <summary>
    /// Checked consumers preserve high address bits through native calls, global storage, pointer arrays and callback values.
    /// </summary>
    /// <param name="checkOverflow">Whether consumer arithmetic is checked by default.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ManagedPointerHighBitsSurviveConsumerArithmetic(bool checkOverflow)
    {
        const string Headers = """
            #include <stdint.h>
            typedef void* (*Echo)(void*);
            typedef struct Slots { void *items[2]; Echo callback; } Slots;
            Slots ankus_pointer_slots;
            void *ankus_pointer_address = (void*)(uintptr_t)UINT64_C(0x8000000123456789);
            void *ankus_pointer_echo_bits(void *value) { return value; }
            Echo ankus_pointer_get_echo(void) { return ankus_pointer_echo_bits; }
            uint64_t ankus_pointer_read_bits(void) { return (uint64_t)(uintptr_t)ankus_pointer_address; }
            """;
        const string Harness = """
            public static class BindingAssertions
            {
                public static unsafe long[] Run()
                {
                    using NativeCallTestBridge.Scope scope = new();
                    void* location = NativeGlobals.ankus_pointer_address;
                    void* direct = NativeMethods.ankus_pointer_echo_bits(location);
                    Echo invoke = NativeMethods.ankus_pointer_get_echo();
                    void* indirect = invoke.Invoke(location);
                    Slots slots = default;
                    slots.items[0] = location;
                    void* restored = slots.items[0];
                    Echo borrowed = new(location);
                    Slots_callbackCallback named = new(location);
                    NativeGlobals.ankus_pointer_address = unchecked((void*)((nuint)location + 3));
                    void** original = NativeGlobals.DangerousAddressOf_ankus_pointer_address();
                    return [unchecked((nint)direct), unchecked((nint)indirect), unchecked((nint)restored),
                        unchecked((nint)borrowed.DangerousGetAddress()), unchecked((nint)named.DangerousGetAddress()),
                        unchecked((nint)(*original)), unchecked((long)NativeMethods.ankus_pointer_read_bits())];
                }
            }
            """;
        const long HighBits = long.MinValue + 0x123456789;
        Assert.AreSequenceEqual<long>([HighBits, HighBits, HighBits, HighBits, HighBits, HighBits + 3, HighBits + 3],
            await ExecuteManagedCallsAsync(Headers, ["ankus_pointer_echo_bits", "ankus_pointer_get_echo", "ankus_pointer_read_bits"],
                Harness, globalNames: ["ankus_pointer_slots", "ankus_pointer_address"], checkOverflow: checkOverflow));
    }
}
