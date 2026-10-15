using System.Buffers.Binary;

namespace Ankus.PgConfig.Tests;

/// <summary>
/// Validates schema extraction against independently encoded native file headers and corruptions.
/// </summary>
[TestClass]
public sealed class NativeSchemaSectionTests
{
    /// <summary>
    /// Returns exact file bytes and measured native identity without relying on the current operating system.
    /// </summary>
    /// <param name="rid">The native library identity.</param>
    [TestMethod]
    [DataRow("linux-x64")]
    [DataRow("linux-arm64")]
    [DataRow("win-x64")]
    [DataRow("osx-x64")]
    [DataRow("osx-arm64")]
    public void ReadsEachPublishedFormat(string rid)
    {
        byte[] payload = [0, 255, 1, 0, 128, 13, 10];
        using var stream = new MemoryStream(Image(rid, payload));
        stream.Position = stream.Length;
        (string actual, byte[] data) = NativeSchemaSection.Read(stream);
        Assert.AreEqual(rid, actual);
        Assert.AreSequenceEqual(payload, data);
        Assert.IsTrue(stream.CanRead);
    }

    /// <summary>
    /// Rejects a target mismatch even when the schema bytes are otherwise valid.
    /// </summary>
    /// <param name="rid">The actual library target.</param>
    [TestMethod]
    [DataRow("linux-x64")]
    [DataRow("win-x64")]
    [DataRow("osx-x64")]
    [DataRow("osx-arm64")]
    public void RejectsWrongRuntimeIdentifier(string rid)
    {
        using var stream = new MemoryStream(Image(rid, [3, 1, 4]));
        Assert.ThrowsExactly<FormatException>(() => NativeSchemaSection.Read(stream, rid == "win-x64" ? "linux-x64" : "win-x64"));
    }

    /// <summary>
    /// An ARM64 library does not satisfy an x64 request, or the reverse, though both are Linux ELF files.
    /// </summary>
    /// <param name="actual">The library's architecture.</param>
    /// <param name="requested">The other Linux architecture.</param>
    [TestMethod]
    [DataRow("linux-arm64", "linux-x64")]
    [DataRow("linux-x64", "linux-arm64")]
    public void RejectsTheOtherLinuxArchitecture(string actual, string requested)
    {
        using var stream = new MemoryStream(Image(actual, [3, 1, 4]));
        Assert.ThrowsExactly<FormatException>(() => NativeSchemaSection.Read(stream, requested));
    }

    /// <summary>
    /// Every missing prefix tail fails with a format error, including the final schema byte.
    /// </summary>
    /// <param name="rid">The native library target.</param>
    [TestMethod]
    [DataRow("linux-x64")]
    [DataRow("win-x64")]
    [DataRow("osx-x64")]
    [DataRow("osx-arm64")]
    public void RejectsEveryTruncatedPrefix(string rid)
    {
        byte[] image = Image(rid, [1, 2, 3]);
        for (int size = 0; size < image.Length; size++)
        {
            using var stream = new MemoryStream(image, 0, size);
            Assert.ThrowsExactly<FormatException>(() => NativeSchemaSection.Read(stream), $"Accepted truncated {rid} image of {size} bytes.");
        }
    }

    /// <summary>
    /// A library must have exactly one file-backed schema section.
    /// </summary>
    /// <param name="rid">The native library target.</param>
    [TestMethod]
    [DataRow("linux-x64")]
    [DataRow("win-x64")]
    [DataRow("osx-x64")]
    [DataRow("osx-arm64")]
    public void RejectsMissingAndDuplicateSections(string rid)
    {
        byte[] image = Image(rid, [7, 8], duplicate: true);
        using var duplicate = new MemoryStream(image);
        Assert.ThrowsExactly<FormatException>(() => NativeSchemaSection.Read(duplicate));

        image = Image(rid, [7, 8]);
        int nameOffset = rid switch { "linux-x64" => 395, "win-x64" => 328, _ => 104 };
        image[nameOffset] = (byte)'?';
        using var missing = new MemoryStream(image);
        Assert.ThrowsExactly<FormatException>(() => NativeSchemaSection.Read(missing));
    }

    /// <summary>
    /// A real native library that Ankus did not build has no schema section and is rejected rather than misread, as
    /// pgrx's <c>parses_managed_postmasters</c> checks against an image it did not produce. The .NET runtime's own
    /// native library is present on every platform.
    /// </summary>
    [TestMethod]
    public void ForeignLibrariesHaveNoSchemaSection()
    {
        string runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        string name = OperatingSystem.IsWindows() ? "coreclr.dll" : OperatingSystem.IsMacOS() ? "libcoreclr.dylib" : "libcoreclr.so";
        using FileStream library = File.OpenRead(Path.Join(runtime, name));
        Assert.ThrowsExactly<FormatException>(() => NativeSchemaSection.Read(library));
    }

    /// <summary>
    /// Supports ELF's extended section count and string-table index encodings.
    /// </summary>
    [TestMethod]
    public void ReadsExtendedElfSectionIndices()
    {
        byte[] payload = [4, 5, 6];
        byte[] image = Image("linux-x64", payload);
        Put16(image, 60, 0);
        Put16(image, 62, ushort.MaxValue);
        Put64(image, 96, 3);
        Put32(image, 104, 1);
        using var stream = new MemoryStream(image);
        Assert.AreSequenceEqual(payload, NativeSchemaSection.Read(stream).Data);
    }

    /// <summary>
    /// Picks the requested architecture rather than the first slice or the reader's current host.
    /// </summary>
    /// <param name="wide">Whether the universal header uses 64-bit ranges.</param>
    /// <param name="rid">The requested target.</param>
    [TestMethod]
    [DataRow(false, "osx-x64")]
    [DataRow(false, "osx-arm64")]
    [DataRow(true, "osx-x64")]
    [DataRow(true, "osx-arm64")]
    public void SelectsUniversalArchitecture(bool wide, string rid)
    {
        using var stream = new MemoryStream(Universal(wide));
        (string actual, byte[] data) = NativeSchemaSection.Read(stream, rid);
        Assert.AreEqual(rid, actual);
        byte[] expected = rid == "osx-x64" ? [11, 12] : [21, 22, 23];
        Assert.AreSequenceEqual(expected, data);
    }

    /// <summary>
    /// Rejects invalid formats, table counts, names, offsets, lengths, flags and unbacked schema storage.
    /// </summary>
    /// <param name="rid">The original valid format.</param>
    /// <param name="offset">The independently specified header field location.</param>
    /// <param name="width">The encoded integer width.</param>
    /// <param name="value">The corrupt little-endian value.</param>
    [TestMethod]
    [DataRow("linux-x64", 4, 1, 1UL)]
    [DataRow("linux-x64", 5, 1, 2UL)]
    [DataRow("linux-x64", 6, 1, 0UL)]
    [DataRow("linux-x64", 16, 2, 1UL)]
    [DataRow("linux-x64", 18, 2, 3UL)]
    [DataRow("linux-x64", 20, 4, 0UL)]
    [DataRow("linux-x64", 40, 8, ulong.MaxValue)]
    [DataRow("linux-x64", 52, 2, 63UL)]
    [DataRow("linux-x64", 58, 2, 63UL)]
    [DataRow("linux-x64", 60, 2, 65535UL)]
    [DataRow("linux-x64", 62, 2, 3UL)]
    [DataRow("linux-x64", 132, 4, 1UL)]
    [DataRow("linux-x64", 160, 8, ulong.MaxValue)]
    [DataRow("linux-x64", 192, 4, 19UL)]
    [DataRow("linux-x64", 196, 4, 8UL)]
    [DataRow("linux-x64", 200, 8, 0x800UL)]
    [DataRow("linux-x64", 200, 8, 4UL)]
    [DataRow("linux-x64", 216, 8, ulong.MaxValue)]
    [DataRow("linux-x64", 224, 8, 0UL)]
    [DataRow("linux-x64", 224, 8, 67108865UL)]
    [DataRow("linux-x64", 402, 1, 65UL)]
    [DataRow("win-x64", 60, 4, 4294967295UL)]
    [DataRow("win-x64", 64, 4, 0UL)]
    [DataRow("win-x64", 68, 2, 0x14cUL)]
    [DataRow("win-x64", 70, 2, 97UL)]
    [DataRow("win-x64", 84, 2, 111UL)]
    [DataRow("win-x64", 86, 2, 0UL)]
    [DataRow("win-x64", 88, 2, 0x10bUL)]
    [DataRow("win-x64", 336, 4, 4UL)]
    [DataRow("win-x64", 344, 4, 0UL)]
    [DataRow("win-x64", 344, 4, 67108865UL)]
    [DataRow("win-x64", 348, 4, 4294967295UL)]
    [DataRow("win-x64", 364, 4, 0x20000040UL)]
    [DataRow("win-x64", 364, 4, 0x80UL)]
    [DataRow("osx-x64", 4, 4, 7UL)]
    [DataRow("osx-x64", 12, 4, 1UL)]
    [DataRow("osx-x64", 16, 4, 4294967295UL)]
    [DataRow("osx-x64", 20, 4, 160UL)]
    [DataRow("osx-x64", 36, 4, 7UL)]
    [DataRow("osx-x64", 36, 4, 144UL)]
    [DataRow("osx-x64", 40, 1, 63UL)]
    [DataRow("osx-x64", 72, 8, ulong.MaxValue)]
    [DataRow("osx-x64", 80, 8, 512UL)]
    [DataRow("osx-x64", 96, 4, 4294967295UL)]
    [DataRow("osx-x64", 120, 1, 63UL)]
    [DataRow("osx-x64", 144, 8, 0UL)]
    [DataRow("osx-x64", 144, 8, ulong.MaxValue)]
    [DataRow("osx-x64", 152, 4, 4294967295UL)]
    [DataRow("osx-x64", 168, 4, 1UL)]
    [DataRow("osx-x64", 168, 4, 0x80000000UL)]
    public void RejectsMalformedImage(string rid, int offset, int width, ulong value)
    {
        byte[] image = Image(rid, [1, 2, 3]);
        for (int index = 0; index < width; index++)
        {
            image[offset + index] = (byte)(value >> (index * 8));
        }

        using var stream = new MemoryStream(image);
        Assert.ThrowsExactly<FormatException>(() => NativeSchemaSection.Read(stream));
    }

    /// <summary>
    /// Rejects absent, overlapping, duplicate, misaligned and dishonest universal architecture records.
    /// </summary>
    /// <param name="wide">Whether architecture offsets use 64-bit values.</param>
    /// <param name="corruption">The invalid universal-header contract.</param>
    [TestMethod]
    [DataRow(false, "unspecified")]
    [DataRow(false, "missing")]
    [DataRow(false, "duplicate")]
    [DataRow(false, "overlap")]
    [DataRow(false, "alignment")]
    [DataRow(false, "mismatch")]
    [DataRow(false, "nested")]
    [DataRow(false, "truncated")]
    [DataRow(true, "unspecified")]
    [DataRow(true, "missing")]
    [DataRow(true, "duplicate")]
    [DataRow(true, "overlap")]
    [DataRow(true, "alignment")]
    [DataRow(true, "mismatch")]
    [DataRow(true, "nested")]
    [DataRow(true, "truncated")]
    public void RejectsMalformedUniversalImage(bool wide, string corruption)
    {
        byte[] image = Universal(wide);
        int second = wide ? 40 : 28;
        switch (corruption)
        {
            case "missing":
                Big32(image, 8, 0x1000012);
                break;
            case "duplicate":
                Big32(image, second, 0x1000007);
                break;
            case "overlap":
                BigRange(image, second + 8, 4096, wide);
                break;
            case "alignment":
                Big32(image, 8 + (wide ? 24 : 16), 13);
                break;
            case "mismatch":
                Put32(image, 4100, 0x100000c);
                break;
            case "nested":
                Big32(image, 4096, 0xcafebabe);
                break;
            case "truncated":
                Array.Resize(ref image, image.Length - 1);
                break;
        }

        using var stream = new MemoryStream(image);
        Assert.ThrowsExactly<FormatException>(() => NativeSchemaSection.Read(stream, corruption == "unspecified" ? null : "osx-x64"));
    }

    private static byte[] Image(string rid, byte[] payload, bool duplicate = false)
    {
        byte[] image = new byte[512 + payload.Length];
        payload.CopyTo(image, 512);
        switch (rid)
        {
            case "linux-x64":
            case "linux-arm64":
                "\u007fELF"u8.CopyTo(image);
                image[4] = 2;
                image[5] = 1;
                image[6] = 1;
                Put16(image, 16, 3);
                Put16(image, 18, rid == "linux-arm64" ? (ushort)183 : (ushort)62);
                Put32(image, 20, 1);
                Put64(image, 40, 64);
                Put16(image, 52, 64);
                Put16(image, 58, 64);
                Put16(image, 60, duplicate ? (ushort)4 : (ushort)3);
                Put16(image, 62, 1);
                Put32(image, 128, 1);
                Put32(image, 132, 3);
                Put64(image, 152, 384);
                Put64(image, 160, 19);
                "\0.shstrtab\0.ankusc\0"u8.CopyTo(image.AsSpan(384));
                Put32(image, 192, 11);
                Put32(image, 196, 1);
                Put64(image, 200, 2);
                Put64(image, 216, 512);
                Put64(image, 224, (ulong)payload.Length);
                if (duplicate)
                {
                    image.AsSpan(192, 64).CopyTo(image.AsSpan(256));
                }

                break;
            case "win-x64":
                Put16(image, 0, 0x5a4d);
                Put32(image, 60, 64);
                Put32(image, 64, 0x4550);
                Put16(image, 68, 0x8664);
                Put16(image, 70, duplicate ? (ushort)2 : (ushort)1);
                Put16(image, 84, 240);
                Put16(image, 86, 0x2002);
                Put16(image, 88, 0x20b);
                ".ankusc"u8.CopyTo(image.AsSpan(328));
                Put32(image, 336, (uint)payload.Length);
                Put32(image, 344, (uint)payload.Length);
                Put32(image, 348, 512);
                Put32(image, 364, 0x40000040);
                if (duplicate)
                {
                    image.AsSpan(328, 40).CopyTo(image.AsSpan(368));
                }

                break;
            default:
                Put32(image, 0, 0xfeedfacf);
                Put32(image, 4, rid == "osx-x64" ? 0x1000007U : 0x100000cU);
                Put32(image, 12, 6);
                Put32(image, 16, 1);
                Put32(image, 20, duplicate ? 232U : 152U);
                Put32(image, 32, 0x19);
                Put32(image, 36, duplicate ? 232U : 152U);
                "__DATA"u8.CopyTo(image.AsSpan(40));
                Put64(image, 80, (ulong)image.Length);
                Put32(image, 96, duplicate ? 2U : 1U);
                "__ankusc"u8.CopyTo(image.AsSpan(104));
                "__DATA"u8.CopyTo(image.AsSpan(120));
                Put64(image, 144, (ulong)payload.Length);
                Put32(image, 152, 512);
                if (duplicate)
                {
                    image.AsSpan(104, 80).CopyTo(image.AsSpan(184));
                }

                break;
        }

        return image;
    }

    private static byte[] Universal(bool wide)
    {
        byte[] x64 = Image("osx-x64", [11, 12]);
        byte[] arm64 = Image("osx-arm64", [21, 22, 23]);
        byte[] image = new byte[8192 + arm64.Length];
        Big32(image, 0, wide ? 0xcafebabfU : 0xcafebabeU);
        Big32(image, 4, 2);
        WriteEntry(8, 0x1000007, 4096, x64.Length);
        WriteEntry(wide ? 40 : 28, 0x100000c, 8192, arm64.Length);
        x64.CopyTo(image, 4096);
        arm64.CopyTo(image, 8192);
        return image;

        void WriteEntry(int start, uint cpu, ulong offset, int size)
        {
            Big32(image, start, cpu);
            BigRange(image, start + 8, offset, wide);
            BigRange(image, start + (wide ? 16 : 12), (ulong)size, wide);
            Big32(image, start + (wide ? 24 : 16), 12);
        }
    }

    private static void Put16(byte[] image, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset), value);

    private static void Put32(byte[] image, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset), value);

    private static void Put64(byte[] image, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(offset), value);

    private static void Big32(byte[] image, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(offset), value);

    private static void BigRange(byte[] image, int offset, ulong value, bool wide)
    {
        if (wide)
        {
            BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(offset), value);
        }
        else
        {
            Big32(image, offset, (uint)value);
        }
    }
}
