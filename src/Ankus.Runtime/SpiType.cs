namespace Ankus;

/// <summary>
/// Converts supported SPI values without reflection or runtime-generated marshaling code.
/// </summary>
internal static class SpiType
{
    /// <summary>
    /// Resolves a statically declared managed type to a built-in PostgreSQL OID.
    /// </summary>
    /// <typeparam name="T">The managed parameter type.</typeparam>
    /// <returns>The PostgreSQL OID.</returns>
    internal static uint GetOid<T>() => GetOid(typeof(T));

    /// <summary>
    /// Preserves explicit composite identities supplied by owned tuples and shaped arrays.
    /// </summary>
    /// <typeparam name="T">The declared managed parameter type.</typeparam>
    /// <param name="value">The parameter value, including a typed NULL.</param>
    /// <returns>The concrete tuple or array identity, or the declared static type identity.</returns>
    internal static uint GetOid<T>(T value) => value switch
    {
        PgHeapTuple tuple => tuple.Descriptor.TypeOid,
        PgArray<PgHeapTuple> array when array.ElementTypeOid != 2249 => NativeBackend.TupleArrayOid(array.ElementTypeOid),
        _ => GetOid<T>(),
    };

    /// <summary>
    /// Resolves a managed type using known type identities without inspecting members or creating types dynamically.
    /// </summary>
    /// <param name="type">The declared managed type.</param>
    /// <returns>The PostgreSQL OID.</returns>
    internal static uint GetOid(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (PgArrayViews.Find(type) is { } view)
        {
            return view.GetOid();
        }

        if (type == typeof(PgInternal))
        {
            return 2281;
        }

        if (type == typeof(PgHeapTuple))
        {
            return 2249;
        }

        if (type == typeof(PgRelation) || type == typeof(PgRelationIdentity) || type == typeof(PgRelationIdentity?))
        {
            return 2205;
        }

        if (type == typeof(PgPoint) || type == typeof(PgPoint?))
        {
            return 600;
        }

        if (type == typeof(PgLineSegment) || type == typeof(PgLineSegment?))
        {
            return 601;
        }

        if (type == typeof(PgPath))
        {
            return 602;
        }

        if (type == typeof(PgBox) || type == typeof(PgBox?))
        {
            return 603;
        }

        if (type == typeof(PgPolygon))
        {
            return 604;
        }

        if (type == typeof(PgLine) || type == typeof(PgLine?))
        {
            return 628;
        }

        if (type == typeof(PgCircle) || type == typeof(PgCircle?))
        {
            return 718;
        }

        if (type == typeof(PgInet) || type == typeof(PgInet?) || type == typeof(System.Net.IPAddress))
        {
            return 869;
        }

        if (type == typeof(PgCidr) || type == typeof(PgCidr?) || type == typeof(System.Net.IPNetwork) || type == typeof(System.Net.IPNetwork?))
        {
            return 650;
        }

        if (type == typeof(bool) || type == typeof(bool?))
        {
            return 16;
        }

        if (type == typeof(byte[]) || type == typeof(PgByteaView))
        {
            return 17;
        }

        if (type == typeof(sbyte) || type == typeof(sbyte?))
        {
            return 18;
        }

        if (type == typeof(long) || type == typeof(long?))
        {
            return 20;
        }

        if (type == typeof(short) || type == typeof(short?))
        {
            return 21;
        }

        if (type == typeof(int) || type == typeof(int?))
        {
            return 23;
        }

        if (type == typeof(string) || type == typeof(PgTextView))
        {
            return 25;
        }

        if (type == typeof(PgCString) || type == typeof(PgCStringView))
        {
            return 2275;
        }

        if (type == typeof(uint) || type == typeof(uint?))
        {
            return 26;
        }

        if (type == typeof(PgItemPointer) || type == typeof(PgItemPointer?))
        {
            return 27;
        }

        if (type == typeof(PgTransactionId) || type == typeof(PgTransactionId?))
        {
            return 28;
        }

        if (type == typeof(float) || type == typeof(float?))
        {
            return 700;
        }

        if (type == typeof(double) || type == typeof(double?))
        {
            return 701;
        }

        if (type == typeof(Guid) || type == typeof(Guid?))
        {
            return 2950;
        }

        if (type == typeof(System.Text.Rune) || type == typeof(System.Text.Rune?))
        {
            return 1043;
        }

        if (type == typeof(PgJson) || type == typeof(PgJson?))
        {
            return 114;
        }

        if (type == typeof(PgJsonb) || type == typeof(PgJsonb?))
        {
            return 3802;
        }

        if (type == typeof(PgNumeric) || type == typeof(PgNumeric?) || type == typeof(decimal) || type == typeof(decimal?))
        {
            return 1700;
        }

        if (type == typeof(PgDate) || type == typeof(PgDate?) || type == typeof(DateOnly) || type == typeof(DateOnly?))
        {
            return 1082;
        }

        if (type == typeof(PgTime) || type == typeof(PgTime?) || type == typeof(TimeOnly) || type == typeof(TimeOnly?))
        {
            return 1083;
        }

        if (type == typeof(PgTimeTz) || type == typeof(PgTimeTz?))
        {
            return 1266;
        }

        if (type == typeof(PgTimestamp) || type == typeof(PgTimestamp?) || type == typeof(DateTime) || type == typeof(DateTime?))
        {
            return 1114;
        }

        if (type == typeof(PgTimestampTz) || type == typeof(PgTimestampTz?) ||
            type == typeof(DateTimeOffset) || type == typeof(DateTimeOffset?))
        {
            return 1184;
        }

        if (type == typeof(PgInterval) || type == typeof(PgInterval?) || type == typeof(TimeSpan) || type == typeof(TimeSpan?))
        {
            return 1186;
        }

        if (PgDatumRegistry.Find(type) is { } datum)
        {
            return datum.GetOid();
        }

        if (PgDatumRegistry.FindArray(type) is { } datumArray)
        {
            return datumArray.GetOid();
        }

        CustomTypeMapping? custom = PgTypeRegistry.Find(type);
        if (custom is not null)
        {
            return custom.GetOid();
        }

        custom = PgTypeRegistry.FindArray(type);
        if (custom is not null)
        {
            return custom.GetArrayOid();
        }

        EnumMapping? enumeration = PgEnumRegistry.Find(type);
        if (enumeration is not null)
        {
            return enumeration.GetOid();
        }

        enumeration = PgEnumRegistry.FindArray(type);
        if (enumeration is not null)
        {
            return enumeration.GetArrayOid();
        }

        uint rangeOid = SpiRange.GetOid(type);
        if (rangeOid != 0)
        {
            return rangeOid;
        }

        uint arrayOid = SpiArray.GetOid(type);
        return arrayOid != 0 ? arrayOid : throw new NotSupportedException($"SPI parameters of managed type '{type}' do not have a registered PostgreSQL conversion.");
    }

    /// <summary>
    /// Copies a managed parameter into an owned native transport value.
    /// </summary>
    /// <param name="value">The managed value.</param>
    /// <param name="customMapping">The declared custom-type mapping when the caller retains it.</param>
    /// <param name="customArrayMapping">The declared custom element mapping for covariant vectors.</param>
    /// <param name="datumMapping">The declared raw converter when binding a mapped scalar parameter.</param>
    /// <param name="datumArrayMapping">The declared mapped array element converter.</param>
    /// <param name="datumTypeOid">The parameter's captured identity, checked against the current mapped type.</param>
    /// <returns>The native value, whose buffers must be released by the caller.</returns>
    internal static NativeValue ToNative(object? value, CustomTypeMapping? customMapping = null, CustomTypeMapping? customArrayMapping = null,
        DatumTypeMapping? datumMapping = null, uint datumTypeOid = 0, DatumArrayMapping? datumArrayMapping = null) => value switch
        {
            _ when datumMapping is not null => datumMapping.Write(value, datumTypeOid),
            _ when datumArrayMapping is not null => datumArrayMapping.Write(value, datumTypeOid),
            _ when customArrayMapping is null && value is not null && PgDatumRegistry.FindArray(value.GetType()) is not null =>
                throw new NotSupportedException("Mapped datum arrays require a declared mapped parameter or generated array slot."),
            PgDatum datum => datum.ToNative(),
            DetachedDatum detached => NativeValue.FromDetachedDatum(detached),
            PgInternal state => state.ToNative(),
            null => new NativeValue { IsNull = 1 },
            _ when customMapping is not null => customMapping.Write(value),
            Array array when customArrayMapping is not null => NativeValue.FromArray(customArrayMapping.Wrap(array)),
            _ when PgTypeRegistry.Find(value.GetType()) is { } custom => custom.Write(value),
            bool boolean => new NativeValue { Integral = boolean ? 1 : 0 },
            sbyte number => new NativeValue { Integral = number },
            short number => new NativeValue { Integral = number },
            int number => new NativeValue { Integral = number },
            long number => new NativeValue { Integral = number },
            uint number => new NativeValue { Integral = number },
            PgItemPointer itemPointer => NativeValue.FromItemPointer(itemPointer),
            PgTransactionId transactionId => NativeValue.FromTransactionId(transactionId),
            float number => new NativeValue { Integral = BitConverter.SingleToInt32Bits(number) },
            double number => new NativeValue { Integral = BitConverter.DoubleToInt64Bits(number) },
            string text => NativeValue.FromString(text),
            PgCString text => NativeValue.FromCString(text),
            Array array when PgEnumRegistry.FindArray(array.GetType()) is { } mapping => NativeValue.FromArray(mapping.Wrap(array)),
            byte[] bytes => NativeValue.FromBytes(bytes),
            Guid uuid => NativeValue.FromGuid(uuid),
            System.Text.Rune rune => NativeValue.FromRune(rune),
            PgInet address => NativeValue.FromInet(address),
            PgPoint point => NativeValue.FromPoint(point),
            PgLine line => NativeValue.FromLine(line),
            PgLineSegment segment => NativeValue.FromLineSegment(segment),
            PgBox box => NativeValue.FromBox(box),
            PgCircle circle => NativeValue.FromCircle(circle),
            PgPath path => NativeValue.FromPath(path),
            PgPolygon polygon => NativeValue.FromPolygon(polygon),
            PgCidr network => NativeValue.FromCidr(network),
            System.Net.IPAddress address => NativeValue.FromInet(new PgInet(address)),
            System.Net.IPNetwork network => NativeValue.FromCidr(new PgCidr(network)),
            PgJson json => NativeValue.FromString(json.Text),
            PgJsonb json => NativeValue.FromString(json.Text),
            PgNumeric number => NativeValue.FromNumeric(number),
            decimal number => NativeValue.FromNumeric(PgNumeric.FromDecimal(number)),
            PgDate date => NativeValue.FromDate(date),
            PgTime time => NativeValue.FromTime(time),
            PgTimeTz time => NativeValue.FromTimeTz(time),
            PgTimestamp timestamp => NativeValue.FromTimestamp(timestamp),
            PgTimestampTz timestamp => NativeValue.FromTimestampTz(timestamp),
            PgInterval interval => NativeValue.FromInterval(interval),
            DateOnly date => NativeValue.FromDate(PgDate.FromDateOnly(date)),
            TimeOnly time => NativeValue.FromTime(PgTime.FromTimeOnly(time)),
            DateTime timestamp => NativeValue.FromTimestamp(PgTimestamp.FromDateTime(timestamp)),
            DateTimeOffset timestamp => NativeValue.FromTimestampTz(PgTimestampTz.FromDateTimeOffset(timestamp)),
            TimeSpan interval => NativeValue.FromInterval(PgInterval.FromTimeSpan(interval)),
            Enum enumeration => NativeValue.FromString(PgEnumRegistry.Require(enumeration.GetType()).ToLabel(enumeration)),
            PgHeapTuple tuple => NativeValue.FromTuple(tuple),
            PgRelation relation => NativeValue.FromRelation(relation),
            PgRelationIdentity relation => new NativeValue { Integral = relation.Oid },
            IPgArray array => NativeValue.FromArray(array),
            IPgRange range => NativeValue.FromRange(range),
            Array array => NativeValue.FromArray(SpiArray.Wrap(array)),
            _ => throw new NotSupportedException("The SPI parameter does not have a registered PostgreSQL conversion."),
        };

    /// <summary>
    /// Copies a native cell into its managed representation using the column's underlying PostgreSQL type.
    /// </summary>
    /// <param name="value">The borrowed native cell.</param>
    /// <param name="oid">The PostgreSQL base type OID.</param>
    /// <returns>The managed value, or null for SQL NULL.</returns>
    internal static object? FromNative(NativeValue value, uint oid)
    {
        if (value.IsNull != 0)
        {
            return null;
        }

        return oid switch
        {
            2281 => value.ReadInternal(),
            16 => value.Integral != 0,
            17 => value.ReadBytes(),
            18 => (sbyte)value.Integral,
            20 => value.Integral,
            21 => (short)value.Integral,
            23 => (int)value.Integral,
            25 or 1042 or 1043 => value.ReadString(),
            2275 => value.ReadCString(),
            26 => (uint)value.Integral,
            2205 => new PgRelationIdentity(checked((uint)value.Integral)),
            27 => value.ReadItemPointer(),
            28 => value.ReadTransactionId(),
            700 => BitConverter.Int32BitsToSingle((int)value.Integral),
            701 => BitConverter.Int64BitsToDouble(value.Integral),
            2950 => value.ReadGuid(),
            869 => value.ReadInet(),
            600 => value.ReadPoint(),
            601 => value.ReadLineSegment(),
            602 => value.ReadPath(),
            603 => value.ReadBox(),
            604 => value.ReadPolygon(),
            628 => value.ReadLine(),
            718 => value.ReadCircle(),
            3904 => value.ReadRange<int>(),
            3926 => value.ReadRange<long>(),
            3906 => value.ReadRange<PgNumeric>(),
            3912 => value.ReadRange<PgDate>(),
            3908 => value.ReadRange<PgTimestamp>(),
            3910 => value.ReadRange<PgTimestampTz>(),
            650 => value.ReadCidr(),
            114 => value.ReadJson(),
            3802 => value.ReadJsonb(),
            1700 => value.ReadNumeric(),
            1082 => value.ReadDate(),
            1083 => value.ReadTime(),
            1266 => value.ReadTimeTz(),
            1114 => value.ReadTimestamp(),
            1184 => value.ReadTimestampTz(),
            1186 => value.ReadInterval(),
            _ when value.IsArray => value.ReadArray(),
            _ when value.IsTuple => value.ReadTuple(),
            _ when value.IsEnum => PgEnumRegistry.FindOid(oid).FromLabel(value.ReadString()),
            _ when value.IsCustomType => PgTypeRegistry.FindOid(oid).Read(value),
            _ when value.IsDetachedDatum => value.ReadDetachedDatum(oid),
            _ => throw new NotSupportedException($"SPI result type OID {oid} does not have a registered managed conversion."),
        };
    }
}
