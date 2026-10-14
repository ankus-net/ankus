using System.Globalization;

namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// Generated record values retain native union overlap, nested array stride, addresses and Boolean storage.
    /// </summary>
    [TestMethod]
    public async Task ManagedRecordsPreserveNativeValues()
    {
        const string Headers = """
            #include <stdint.h>
            #include <stddef.h>
            typedef union Payload { uint32_t bits; unsigned short halves[2]; } Payload;
            typedef struct Record {
                unsigned char lead;
                Payload payload;
                unsigned short grid[2][3];
                const struct Record *next;
                _Bool flag;
            } Record;
            extern Record observed;
            """;
        const string Main = """
            #include <stdio.h>
            int main(void) {
                Record value = {0};
                value.payload.halves[0] = 11;
                value.payload.halves[1] = 22;
                value.grid[1][2] = 65530;
                value.next = (const Record *)(uintptr_t)0x1020;
                value.flag = 1;
                printf("%zu %zu %zu %zu %zu %zu %u %u %zu %u %zu %zu\n",
                    sizeof(Record), sizeof(Payload), offsetof(Record, payload), offsetof(Record, grid),
                    offsetof(Record, next), offsetof(Record, flag), value.payload.bits,
                    (unsigned int)value.grid[1][2], (size_t)value.next, (unsigned int)value.flag,
                    sizeof(Record), _Alignof(Record));
                return 0;
            }
            """;
        const string Harness = """
            using System;
            using Ankus;
            using Ankus.Postgres;
            public static class BindingAssertions
            {
                public static unsafe long[] Run()
                {
                    Record value = default;
                    value.payload.halves[0] = 11;
                    value.payload.halves[1] = 22;
                    value.grid[1][2] = 65530;
                    value.next = (Record*)0x1020;
                    value.flag = true;
                    return [sizeof(Record), sizeof(Payload),
                        (byte*)&value.payload - (byte*)&value, (byte*)&value.grid - (byte*)&value,
                        (byte*)&value.next - (byte*)&value, (byte*)&value.flag - (byte*)&value,
                        value.payload.bits, value.grid[1][2], (nint)value.next, value.flag ? 1 : 0,
                        NativeSize<Record>(), NativeAlignment<Record>()];
                }
                private static int NativeSize<T>() where T : unmanaged, IPgNativeType => T.NativeSize;
                private static int NativeAlignment<T>() where T : unmanaged, IPgNativeType => T.NativeAlignment;
            }
            """;
        string directory = Path.Join(Path.GetTempPath(), "ankus-managed-record-" + Guid.NewGuid().ToString("N"));
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
    /// Signed bitfield boundaries retain their exact native bytes without overwriting adjacent bits or padding.
    /// </summary>
    [TestMethod]
    public async Task ManagedBitfieldsPreserveNativeStorage()
    {
        const string Headers = """
            typedef struct Flags {
                unsigned int low : 3;
                signed int high : 5;
                unsigned int middle : 10;
                unsigned int : 0;
                _Bool tail : 1;
                unsigned char last;
            } Flags;
            extern Flags observed;
            """;
        const string Main = """
            #include <stdio.h>
            #include <string.h>
            int main(void) {
                Flags value;
                memset(&value, 0xa5, sizeof(value));
                value.low = 7; value.high = -16; value.middle = 1023; value.tail = 1;
                printf("%u %d %u %d ", value.low, value.high, value.middle, value.tail);
                for (size_t i = 0; i < sizeof(value); ++i) printf("%u ", ((unsigned char *)&value)[i]);
                value.high = 15; value.low = 0; value.middle = 0; value.tail = 0;
                printf("%u %d %u %d ", value.low, value.high, value.middle, value.tail);
                for (size_t i = 0; i < sizeof(value); ++i) printf("%u ", ((unsigned char *)&value)[i]);
                return 0;
            }
            """;
        const string Harness = """
            using System;
            using System.Collections.Generic;
            using Ankus.Postgres;
            public static class BindingAssertions
            {
                public static unsafe long[] Run()
                {
                    Flags value = default;
                    Span<byte> bytes = new(&value, sizeof(Flags));
                    bytes.Fill(0xa5);
                    value.low = 7; value.high = -16; value.middle = 1023; value.tail = true;
                    List<long> result = [value.low, value.high, value.middle, value.tail ? 1 : 0];
                    foreach (byte item in bytes) result.Add(item);
                    byte[] before = bytes.ToArray();
                    int rejected = 0;
                    try { value.low = 8; } catch (ArgumentOutOfRangeException error) when (error.ParamName == "value") { rejected++; }
                    try { value.high = -17; } catch (ArgumentOutOfRangeException error) when (error.ParamName == "value") { rejected++; }
                    try { value.high = 16; } catch (ArgumentOutOfRangeException error) when (error.ParamName == "value") { rejected++; }
                    try { value.middle = 1024; } catch (ArgumentOutOfRangeException error) when (error.ParamName == "value") { rejected++; }
                    if (rejected != 4 || !bytes.SequenceEqual(before)) throw new InvalidOperationException("Rejected writes changed native storage.");
                    value.high = 15; value.low = 0; value.middle = 0; value.tail = false;
                    result.AddRange([value.low, value.high, value.middle, value.tail ? 1 : 0]);
                    foreach (byte item in bytes) result.Add(item);
                    return result.ToArray();
                }
            }
            """;
        string directory = Path.Join(Path.GetTempPath(), "ankus-managed-bitfield-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            NativeRecordGraph graph = await CollectRecordsAsync(Headers, [new("observed", "observed", false)], directory);
            NativeBindingSource binding = NativeBindingRecordCSharp.Generate(graph);
            long[] expected = await RunRecordWitnessAsync(Headers + "\n" + Main, directory);
            Assert.AreSequenceEqual(expected, GeneratedBindingCompilation.Run(binding, Harness, context.CancellationToken));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Anonymous containers promote usable fields while separate anonymous declarations retain distinct managed types.
    /// </summary>
    [TestMethod]
    public async Task ManagedAnonymousRecordsRetainIdentity()
    {
        const string Headers = """
            #include <stdint.h>
            typedef enum Limit { Low = -9, High = 17 } Limit;
            typedef enum Wide { Full = UINT64_MAX } Wide;
            typedef struct Container {
                union { struct { short first; unsigned short second; }; unsigned int combined; };
                struct { int count; } left;
                struct { int count; } right;
                Limit limit;
                Wide wide;
            } Container;
            extern Container observed;
            """;
        const string Main = """
            #include <stdio.h>
            int main(void) {
                Container value = {0};
                value.first = -7; value.second = 65000;
                value.left.count = 45; value.right.count = 67;
                value.limit = Low; value.wide = Full;
                printf("%zu %d %u %u %d %d %d %d %zu %d\n", sizeof(value), value.first,
                    (unsigned int)value.second, value.combined, value.left.count, value.right.count,
                    (int)value.limit, __builtin_types_compatible_p(__typeof__(value.left), __typeof__(value.right)),
                    sizeof(value.wide), (uint64_t)value.wide == UINT64_MAX);
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
                    Container value = default;
                    value.first = -7; value.second = 65000;
                    value.left.count = 45; value.right.count = 67;
                    value.limit = Limit.Low; value.wide = Wide.Full;
                    return [sizeof(Container), value.first, value.second, value.combined,
                        value.left.count, value.right.count, (int)value.limit,
                        value.left.GetType() == value.right.GetType() ? 1 : 0,
                        sizeof(Wide), (ulong)value.wide == ulong.MaxValue ? 1 : 0];
                }
            }
            """;
        string directory = Path.Join(Path.GetTempPath(), "ankus-managed-anonymous-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            NativeRecordGraph graph = await CollectRecordsAsync(Headers, [new("observed", "observed", false)], directory);
            NativeBindingSource binding = NativeBindingRecordCSharp.Generate(graph);
            long[] expected = await RunRecordWitnessAsync(Headers + "\n" + Main, directory);
            Assert.AreSequenceEqual(expected, GeneratedBindingCompilation.Run(binding, Harness, context.CancellationToken));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// A native long-double value retains all representation bytes and over-aligned field placement without narrowing.
    /// </summary>
    [TestMethod]
    public async Task ManagedExtendedValuesRetainRepresentation()
    {
        const string Headers = "typedef struct Extended { unsigned char marker; _Alignas(32) long double value; } Extended; extern Extended observed;";
        const string Main = """
            #include <stdio.h>
            #include <stddef.h>
            #include <string.h>
            #include <float.h>
            int main(void) {
                Extended value;
                memset(&value, 0x5a, sizeof(value));
                value.marker = 93;
                value.value = 1.0L + LDBL_EPSILON;
                size_t offset = offsetof(Extended, value);
                printf("%zu %zu %zu %zu %u %u %u ", sizeof(value), offset, _Alignof(Extended), sizeof(value.value),
                    (unsigned int)value.marker, ((unsigned char *)&value)[offset - 1],
                    ((unsigned char *)&value)[offset + sizeof(value.value)]);
                for (size_t i = 0; i < sizeof(value.value); ++i) printf("%u ", ((unsigned char *)&value.value)[i]);
                return 0;
            }
            """;
        const string Harness = """
            using System;
            using System.Collections.Generic;
            using System.Runtime.InteropServices;
            using Ankus;
            using Ankus.Postgres;
            public static class BindingAssertions
            {
                public static unsafe long[] Run()
                {
                    Extended value = default;
                    Span<byte> all = new(&value, sizeof(Extended));
                    all.Fill(0x5a);
                    Span<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value.value, 1));
                    ReadOnlySpan<byte> native = [__NATIVE_BYTES__];
                    native.CopyTo(bytes);
                    value.marker = 93;
                    long offset = (byte*)&value.value - (byte*)&value;
                    List<long> result = [sizeof(Extended), offset, Alignment<Extended>(), bytes.Length,
                        value.marker, all[(int)offset - 1], all[(int)offset + bytes.Length]];
                    foreach (byte item in bytes) result.Add(item);
                    return result.ToArray();
                }
                private static int Alignment<T>() where T : unmanaged, IPgNativeType => T.NativeAlignment;
            }
            """;
        string directory = Path.Join(Path.GetTempPath(), "ankus-managed-extended-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            NativeRecordGraph graph = await CollectRecordsAsync(Headers, [new("observed", "observed", false)], directory);
            NativeBindingSource binding = NativeBindingRecordCSharp.Generate(graph);
            long[] expected = await RunRecordWitnessAsync(Headers + "\n" + Main, directory);
            string bytes = string.Join(',', expected.Skip(7).Select(static value => value.ToString(CultureInfo.InvariantCulture)));
            string harness = Harness.Replace("__NATIVE_BYTES__", bytes, StringComparison.Ordinal);
            Assert.AreSequenceEqual(expected, GeneratedBindingCompilation.Run(binding, harness, context.CancellationToken));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Opaque addresses remain nonallocatable and flexible tails require explicit live storage and checked extents.
    /// </summary>
    [TestMethod]
    public async Task ManagedRecordsRetainIncompleteStorage()
    {
        const string Headers = """
            #include <stdint.h>
            struct Opaque;
            typedef struct Tail { struct Opaque *owner; uint32_t length; unsigned char data[]; } Tail;
            extern Tail observed;
            """;
        const string Main = """
            #include <stdio.h>
            #include <stddef.h>
            #include <stdlib.h>
            int main(void) {
                Tail *value = calloc(1, sizeof(Tail) + 3);
                if (value == NULL) return 1;
                value->data[0] = 91; value->data[2] = 93;
                printf("%zu %zu %u %u\n", sizeof(Tail), offsetof(Tail, data), value->data[0], value->data[2]);
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
                    if (!typeof(Opaque).IsValueType || typeof(Ankus.IPgNativeType).IsAssignableFrom(typeof(Opaque)) || Opaque.IsComplete || Opaque.NativeSize != -1)
                        throw new InvalidOperationException("An opaque native declaration became allocatable.");
                    byte* address = stackalloc byte[sizeof(Tail) + 3];
                    new Span<byte>(address, sizeof(Tail) + 3).Clear();
                    Span<byte> data = Tail.Dangerous_data((Tail*)address, 3);
                    data[0] = 91; data[2] = 93;
                    int rejected = 0;
                    try { Tail.Dangerous_data(null, 0); } catch (ArgumentNullException error) when (error.ParamName == "address") { rejected++; }
                    try { Tail.Dangerous_data((Tail*)address, -1); } catch (ArgumentOutOfRangeException error) when (error.ParamName == "length") { rejected++; }
                    try { Tail.Dangerous_data((Tail*)(-1), 1); } catch (OverflowException) { rejected++; }
                    if (rejected != 3 || Tail.Dangerous_data((Tail*)address, 0).Length != 0)
                        throw new InvalidOperationException("A native tail accepted an invalid extent.");
                    fixed (byte* start = data)
                    {
                        nuint high = (nuint)1 << (IntPtr.Size * 8 - 1);
                        Span<byte> empty = Tail.Dangerous_data((Tail*)high, 0);
                        nuint location = (nuint)System.Runtime.CompilerServices.Unsafe.AsPointer(ref System.Runtime.InteropServices.MemoryMarshal.GetReference(empty));
                        if (location != high + (nuint)(start - address)) throw new InvalidOperationException("Native address bits were lost.");
                        return [sizeof(Tail), start - address, data[0], data[2]];
                    }
                }
            }
            """;
        string directory = Path.Join(Path.GetTempPath(), "ankus-managed-incomplete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            NativeRecordGraph graph = await CollectRecordsAsync(Headers, [new("observed", "observed", false)], directory);
            NativeBindingSource binding = NativeBindingRecordCSharp.Generate(graph);
            long[] expected = await RunRecordWitnessAsync(Headers + "\n" + Main, directory);
            Assert.AreSequenceEqual(expected, GeneratedBindingCompilation.Run(binding, Harness, context.CancellationToken));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// C identifiers remain usable after CLR name collisions, and promoted flexible tails use cumulative native offsets.
    /// </summary>
    [TestMethod]
    public async Task ManagedRecordNamesAndPromotedTailsPreserveValues()
    {
        const string Headers = """
            #include <stdint.h>
            enum Limits { Limits = -1, Native_Limits = 3, value__ = 5 };
            struct Collision {
                int Collision;
                int Native_Collision;
                int ToString;
                enum Limits limit;
                union { struct { unsigned int low : 3; signed int high : 5; }; unsigned int combined; };
                struct { int count; unsigned char data[]; };
            };
            extern struct Collision observed;
            """;
        const string Main = """
            #include <stdio.h>
            #include <stdlib.h>
            #include <stddef.h>
            int main(void) {
                struct Collision *value = calloc(1, sizeof(*value) + 3);
                if (value == NULL) return 2;
                value->Collision = 7; value->Native_Collision = 9; value->ToString = 11;
                value->limit = Limits; value->low = 5; value->high = -13;
                value->count = 3; value->data[2] = 99;
                printf("%zu %d %d %d %d %u %d %u %d %zu %d %d %d\n", sizeof(*value),
                    value->Collision, value->Native_Collision, value->ToString, (int)value->limit,
                    value->low, value->high, value->combined, value->count, offsetof(struct Collision, data),
                    value->data[2], (int)Native_Limits, (int)value__);
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
                    byte* bytes = stackalloc byte[sizeof(Collision) + 3];
                    new Span<byte>(bytes, sizeof(Collision) + 3).Clear();
                    Collision* value = (Collision*)bytes;
                    value->Native_Collision_1 = 7; value->Native_Collision = 9; value->ToString = 11;
                    value->limit = Limits.Native_Limits_1; value->low = 5; value->high = -13;
                    value->count = 3;
                    Span<byte> data = Collision.Dangerous_data(value, 3);
                    data[2] = 99;
                    fixed (byte* start = data)
                        return [sizeof(Collision), value->Native_Collision_1, value->Native_Collision, value->ToString,
                            (int)value->limit, value->low, value->high, value->combined, value->count,
                            start - bytes, data[2], (int)Limits.Native_Limits, (int)Limits.Native_value__];
                }
            }
            """;
        string directory = Path.Join(Path.GetTempPath(), "ankus-managed-names-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            NativeRecordGraph graph = await CollectRecordsAsync(Headers, [new("observed", "observed", false)], directory);
            NativeBindingSource binding = NativeBindingRecordCSharp.Generate(graph);
            long[] expected = await RunRecordWitnessAsync(Headers + "\n" + Main, directory);
            Assert.AreSequenceEqual(expected, GeneratedBindingCompilation.Run(binding, Harness, context.CancellationToken));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Full-width and narrower 64-bit fields retain extrema, signed extension and exact storage across byte boundaries.
    /// </summary>
    [TestMethod]
    public async Task ManagedWideBitfieldsPreserveNativeExtrema()
    {
        const string Headers = """
            #include <stdint.h>
            enum State { First = -4, Last = 3 };
            struct WideBits {
                unsigned long long whole : 64;
                signed long long negative : 64;
                unsigned long long limited : 63;
                enum State state : 3;
            };
            extern struct WideBits observed;
            """;
        const string Main = """
            #include <stdio.h>
            #include <string.h>
            int main(void) {
                struct WideBits value;
                memset(&value, 0x5a, sizeof(value));
                value.whole = UINT64_MAX; value.negative = INT64_MIN; value.limited = INT64_MAX; value.state = Last;
                printf("%d %lld %lld %d ", value.whole == UINT64_MAX, (long long)value.negative, (long long)value.limited, (int)value.state);
                for (size_t i = 0; i < sizeof(value); ++i) printf("%u ", ((unsigned char *)&value)[i]);
                value.whole = 0; value.negative = INT64_MAX; value.limited = 0; value.state = First;
                printf("%llu %lld %llu %d ", (unsigned long long)value.whole, (long long)value.negative, (unsigned long long)value.limited, (int)value.state);
                for (size_t i = 0; i < sizeof(value); ++i) printf("%u ", ((unsigned char *)&value)[i]);
                return 0;
            }
            """;
        const string Harness = """
            using System;
            using System.Collections.Generic;
            using Ankus.Postgres;
            public static class BindingAssertions
            {
                public static unsafe long[] Run()
                {
                    WideBits value = default;
                    Span<byte> bytes = new(&value, sizeof(WideBits));
                    bytes.Fill(0x5a);
                    value.whole = ulong.MaxValue; value.negative = long.MinValue; value.limited = long.MaxValue; value.state = State.Last;
                    List<long> result = [value.whole == ulong.MaxValue ? 1 : 0, value.negative, (long)value.limited, (int)value.state];
                    foreach (byte item in bytes) result.Add(item);
                    byte[] before = bytes.ToArray();
                    int rejected = 0;
                    try { value.limited = (ulong)long.MaxValue + 1; } catch (ArgumentOutOfRangeException error) when (error.ParamName == "value") { rejected++; }
                    try { value.state = (State)4; } catch (ArgumentOutOfRangeException error) when (error.ParamName == "value") { rejected++; }
                    try { value.state = (State)(-5); } catch (ArgumentOutOfRangeException error) when (error.ParamName == "value") { rejected++; }
                    if (rejected != 3 || !bytes.SequenceEqual(before)) throw new InvalidOperationException("A rejected wide field changed storage.");
                    value.whole = 0; value.negative = long.MaxValue; value.limited = 0; value.state = State.First;
                    result.AddRange([(long)value.whole, value.negative, (long)value.limited, (int)value.state]);
                    foreach (byte item in bytes) result.Add(item);
                    return result.ToArray();
                }
            }
            """;
        string directory = Path.Join(Path.GetTempPath(), "ankus-managed-wide-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            NativeRecordGraph graph = await CollectRecordsAsync(Headers, [new("observed", "observed", false)], directory);
            NativeBindingSource binding = NativeBindingRecordCSharp.Generate(graph);
            long[] expected = await RunRecordWitnessAsync(Headers + "\n" + Main, directory);
            Assert.AreSequenceEqual(expected, GeneratedBindingCompilation.Run(binding, Harness, context.CancellationToken));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    private async Task<long[]> RunRecordWitnessAsync(string source, string directory)
    {
        string file = Path.Join(directory, "witness.c");
        string executable = Path.Join(directory, OperatingSystem.IsWindows() ? "witness.exe" : "witness");
        await File.WriteAllTextAsync(file, source, context.CancellationToken);
        string compiler = OperatingSystem.IsWindows() ? "clang-cl.exe" : "cc";
        string[] arguments = OperatingSystem.IsWindows()
            ? ["/nologo", "/std:c11", "/W4", "/WX", "/O2", "/Fe" + executable, "/Fo" + Path.ChangeExtension(executable, ".obj"), file]
            : ["-std=c11", "-Wall", "-Wextra", "-Werror", "-O2", file, "-o", executable];
        await RunAsync(compiler, arguments, directory);
        return [.. (await RunAsync(executable, [], directory)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(static value => long.Parse(value, CultureInfo.InvariantCulture))];
    }

    /// <summary>
    /// Native 128-bit values and bitfields preserve both halves, sign extension and rejected out-of-range writes.
    /// </summary>
    [TestMethod]
    public async Task ManagedHugeIntegersPreserveNativeBits()
    {
        const string Headers = """
            struct Huge {
                unsigned __int128 whole;
                signed __int128 negative : 128;
                unsigned __int128 limited : 127;
            };
            extern struct Huge observed;
            """;
        const string Main = """
            #include <stdio.h>
            #include <string.h>
            int main(void) {
                struct Huge value;
                memset(&value, 0xa5, sizeof(value));
                value.whole = ~(unsigned __int128)0;
                value.negative = -((signed __int128)1 << 126);
                value.negative += value.negative;
                value.limited = ((unsigned __int128)1 << 127) - 1;
                printf("%d %d %d ", value.whole == ~(unsigned __int128)0, value.negative < 0,
                    value.limited == ((unsigned __int128)1 << 127) - 1);
                for (size_t i = 0; i < sizeof(value); ++i) printf("%u ", ((unsigned char *)&value)[i]);
                value.whole = 0; value.negative = (signed __int128)(((unsigned __int128)1 << 127) - 1); value.limited = 0;
                for (size_t i = 0; i < sizeof(value); ++i) printf("%u ", ((unsigned char *)&value)[i]);
                return 0;
            }
            """;
        const string Harness = """
            using System;
            using System.Collections.Generic;
            using Ankus.Postgres;
            public static class BindingAssertions
            {
                public static unsafe long[] Run()
                {
                    Huge value = default;
                    Span<byte> bytes = new(&value, sizeof(Huge));
                    bytes.Fill(0xa5);
                    value.whole = UInt128.MaxValue; value.negative = Int128.MinValue; value.limited = (UInt128)Int128.MaxValue;
                    List<long> result = [value.whole == UInt128.MaxValue ? 1 : 0, value.negative < 0 ? 1 : 0,
                        value.limited == (UInt128)Int128.MaxValue ? 1 : 0];
                    if (value.negative != Int128.MinValue) throw new InvalidOperationException("A signed 128-bit value was narrowed.");
                    foreach (byte item in bytes) result.Add(item);
                    byte[] before = bytes.ToArray();
                    bool rejected = false;
                    try { value.limited = (UInt128)Int128.MaxValue + 1; } catch (ArgumentOutOfRangeException error) when (error.ParamName == "value") { rejected = true; }
                    if (!rejected || !bytes.SequenceEqual(before)) throw new InvalidOperationException("A rejected 128-bit field changed storage.");
                    value.whole = 0; value.negative = Int128.MaxValue; value.limited = 0;
                    if (value.negative != Int128.MaxValue) throw new InvalidOperationException("A positive 128-bit value was narrowed.");
                    foreach (byte item in bytes) result.Add(item);
                    return result.ToArray();
                }
            }
            """;
        string directory = Path.Join(Path.GetTempPath(), "ankus-managed-huge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            NativeRecordGraph graph = await CollectRecordsAsync(Headers, [new("observed", "observed", false)], directory);
            NativeBindingSource binding = NativeBindingRecordCSharp.Generate(graph);
            long[] expected = await RunRecordWitnessAsync(Headers + "\n" + Main, directory);
            Assert.AreSequenceEqual(expected, GeneratedBindingCompilation.Run(binding, Harness, context.CancellationToken));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }
}
