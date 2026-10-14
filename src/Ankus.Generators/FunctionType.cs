using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Describes the SQL type and native conversion contract for a generated method parameter or result.
/// </summary>
internal sealed record FunctionType
{
    /// <summary>
    /// Creates the non-null boolean result of a generated comparison.
    /// </summary>
    internal static FunctionType ComparisonResult() => new("bool", "boolean", "BOOL", "Bool", "Integral", false, false);

    /// <summary>
    /// Creates the non-null signed integer result of a generated index support function.
    /// </summary>
    internal static FunctionType IndexSupportResult() => new("int", "integer", "INT32", "Int32", "Integral", false, false);

    /// <summary>
    /// Creates the JSONB transport used by generated benchmark wrappers.
    /// </summary>
    /// <param name="nullable">Whether SQL NULL is accepted.</param>
    /// <returns>The built-in JSONB conversion contract.</returns>
    internal static FunctionType Jsonb(bool nullable = false)
        => new("global::Ankus.PgJsonb", "jsonb", "jsonb", "jsonb", string.Empty, nullable, false);

    /// <summary>
    /// Creates an already validated custom type's non-null I/O conversion without repeating semantic analysis.
    /// </summary>
    /// <param name="type">The detached payload and catalog identity.</param>
    /// <returns>The scalar conversion used by generated input and output functions.</returns>
    internal static FunctionType CustomIo(CustomTypeIoModel type) => new(type.Managed, type.Sql, "custom", "custom", string.Empty, false, !type.IsValueType)
    {
        CustomType = type.Type,
    };

    private FunctionType(string managed, string sql, string reader, string writer, string field, bool nullable, bool reference)
    {
        Managed = managed;
        Sql = sql;
        Reader = reader;
        Writer = writer;
        Field = field;
        Nullable = nullable;
        Reference = reference;
    }

    /// <summary>
    /// Gets the non-nullable managed type spelling.
    /// </summary>
    internal string Managed { get; }

    /// <summary>
    /// Gets the SQL type spelling.
    /// </summary>
    internal string Sql { get; }

    /// <summary>
    /// Gets the PostgreSQL argument access macro, or the varlena conversion category.
    /// </summary>
    internal string Reader { get; }

    /// <summary>
    /// Gets the PostgreSQL datum constructor, or the varlena conversion category.
    /// </summary>
    internal string Writer { get; }

    /// <summary>
    /// Gets the managed transport property used for scalar conversion.
    /// </summary>
    internal string Field { get; }

    /// <summary>
    /// Gets whether SQL NULL is accepted as a managed argument.
    /// </summary>
    internal bool Nullable { get; }

    /// <summary>
    /// Gets whether the managed representation is a reference type.
    /// </summary>
    internal bool Reference { get; }

    /// <summary>
    /// Gets the scalar element contract for a vector or shape-preserving array.
    /// </summary>
    internal FunctionType? Element
    {
        get;
        private init;
    }

    /// <summary>
    /// Gets the scalar subtype of a built-in range, independently of an array's element contract.
    /// </summary>
    internal FunctionType? RangeSubtype
    {
        get;
        private init;
    }

    /// <summary>
    /// Gets the generated enum contract when this is a user-defined enum scalar.
    /// </summary>
    internal EnumDeclaration? Enumeration
    {
        get;
        private init;
    }

    /// <summary>
    /// Gets the generated binary storage contract for a custom base type.
    /// </summary>
    internal CustomTypeReference? CustomType
    {
        get;
        private init;
    }

    /// <summary>
    /// Gets the reusable raw datum conversion contract for this managed scalar.
    /// </summary>
    internal DatumTypeReference? DatumType
    {
        get;
        private init;
    }

    /// <summary>
    /// Gets the optional named binding for a composite or raw scalar.
    /// </summary>
    internal SqlTypeReference? Binding
    {
        get;
        private init;
    }

    /// <summary>
    /// Gets whether this scalar carries an owned PostgreSQL composite or anonymous record.
    /// </summary>
    internal bool IsComposite => Reader == "tuple";

    /// <summary>
    /// Gets whether PostgreSQL resolves this scalar's concrete type at the call site.
    /// </summary>
    internal bool IsPolymorphic => Reader is "anyelement" or "anyarray";

    /// <summary>
    /// Gets whether the resolved array is exposed through a checked borrowed native view.
    /// </summary>
    internal bool IsBorrowedArray => Managed == "global::Ankus.PgArrayView" || Reader == "borrowedarray";

    /// <summary>
    /// Gets whether a fixed SQL text, bytea or C-string type uses a checked borrowed native view.
    /// </summary>
    internal bool IsBorrowedBuffer => Managed is "global::Ankus.PgByteaView" or "global::Ankus.PgTextView" or "global::Ankus.PgCStringView";

    /// <summary>
    /// Gets whether the scalar uses an explicitly bound raw datum.
    /// </summary>
    internal bool IsRaw => Reader == "datum";

    /// <summary>
    /// Gets whether a scalar is a checked native-layout varlena view.
    /// </summary>
    internal bool IsVarlena => Reader == "varlena";

    /// <summary>
    /// Gets whether the requested scalar or array uses a registered datum converter.
    /// </summary>
    internal bool IsMapped => DatumType is not null || Element?.DatumType is not null;

    /// <summary>
    /// Gets whether input and output transport preserve raw storage and exact SQL type identity.
    /// </summary>
    internal bool UsesRawTransport => IsRaw || IsPolymorphic || IsMapped || IsBorrowedBuffer || IsBorrowedArray;

    /// <summary>
    /// Gets whether the SQL declaration uses a polymorphic type, including an explicit raw binding.
    /// </summary>
    internal bool IsSqlPolymorphic => IsPolymorphic || IsRaw && Binding?.IsPolymorphic == true;

    /// <summary>
    /// Gets whether the SQL declaration uses internal, including an explicit raw binding.
    /// </summary>
    internal bool IsSqlInternal => IsInternal || IsRaw && Binding?.IsInternal == true;

    /// <summary>
    /// Gets whether the value carries opaque PostgreSQL internal state.
    /// </summary>
    internal bool IsInternal => Reader == "internal";

    /// <summary>
    /// Gets whether this scalar owns a native relation reference for a regclass datum.
    /// </summary>
    internal bool IsRelation => Managed == "global::Ankus.PgRelation";

    /// <summary>
    /// Gets whether this scalar or array carries explicitly disposable relation references.
    /// </summary>
    internal bool HasRelations => !IsBorrowedArray && (IsRelation || Element?.IsRelation == true);

    /// <summary>
    /// Gets the nullable-aware element spelling used by generated generic adapters.
    /// </summary>
    internal string ElementManaged => Element!.Managed + (Element.Nullable ? "?" : string.Empty);

    /// <summary>
    /// Gets whether this array is represented by an ordinary managed vector.
    /// </summary>
    internal bool IsVector
    {
        get;
        private init;
    }

    /// <summary>
    /// Gets whether this type uses a variable-length native buffer.
    /// </summary>
    internal bool IsBuffer => !UsesRawTransport && !IsInternal && !IsRelation && (Enumeration is not null || CustomType is not null || Reference || GeometryName.Length != 0 || Reader is "uuid" or "json" or "jsonb" or "numeric" or "inet" or "cidr" or "varchar");

    /// <summary>
    /// Gets the statically supported geometric transport method suffix.
    /// </summary>
    internal string GeometryName => Reader switch
    {
        "point" => "Point",
        "lseg" => "LineSegment",
        "line" => "Line",
        "box" => "Box",
        "circle" => "Circle",
        "path" => "Path",
        "polygon" => "Polygon",
        _ => string.Empty,
    };

    /// <summary>
    /// Gets whether the type uses the field-wise temporal transport.
    /// </summary>
    internal bool IsTemporal => Reader is "date" or "time" or "timetz" or "timestamp" or "timestamptz" or "interval";

    /// <summary>
    /// Gets the temporal reader/writer suffix on NativeValue.
    /// </summary>
    internal string TemporalName => Reader switch
    {
        "date" => "Date",
        "time" => "Time",
        "timetz" => "TimeTz",
        "timestamp" => "Timestamp",
        "timestamptz" => "TimestampTz",
        "interval" => "Interval",
        _ => string.Empty,
    };

    /// <summary>
    /// Gets the optional .NET conversion suffix for a built-in temporal type.
    /// </summary>
    internal string ClrTemporalName => Managed.StartsWith("global::System.", System.StringComparison.Ordinal)
        ? Managed.Substring("global::System.".Length) : string.Empty;

    /// <summary>
    /// Gets the native built-in OID macro for typed datum conversion.
    /// </summary>
    internal string BufferOid => Reader == "cstring_bytes" ? "CSTRINGOID" : Reader.ToUpperInvariant() + "OID";

    /// <summary>
    /// Resolves a Roslyn type, including nullable value and reference annotations, to a SQL conversion contract.
    /// </summary>
    /// <param name="type">The managed type symbol.</param>
    /// <param name="binding">The optional named SQL binding for this scalar or composite array element.</param>
    /// <returns>The conversion contract, or null when the type needs an additional converter.</returns>
    internal static FunctionType? Create(ITypeSymbol type, SqlTypeReference? binding = null)
    {
        bool nullable = type.NullableAnnotation == NullableAnnotation.Annotated;
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } optional)
        {
            type = optional.TypeArguments[0];
            nullable = true;
        }

        if (type is IArrayTypeSymbol { Rank: 1, IsSZArray: true, ElementType.SpecialType: SpecialType.System_Byte })
        {
            return new("byte[]", "bytea", "bytea", "bytea", string.Empty, nullable, reference: true);
        }

        bool borrowedArray = type is INamedTypeSymbol { Name: "PgArrayView", Arity: 1 } borrowed &&
            borrowed.ContainingNamespace.ToDisplayString() == "Ankus";
        ITypeSymbol? elementType = type switch
        {
            IArrayTypeSymbol { Rank: 1, IsSZArray: true } array => array.ElementType,
            INamedTypeSymbol { Name: "PgArray" or "PgArrayView", Arity: 1 } array when array.ContainingNamespace.ToDisplayString() == "Ankus"
                => array.TypeArguments[0],
            _ => null,
        };
        if (elementType is not null)
        {
            FunctionType? element = Create(elementType, binding);
            if (element is null || element.Element is not null ||
                element.UsesRawTransport && element.DatumType is null && !(borrowedArray && element.IsBorrowedBuffer) ||
                element.IsInternal || element.Managed == "void")
            {
                return null;
            }

            bool vector = type is IArrayTypeSymbol;
            string elementName = element.Managed + (element.Nullable ? "?" : string.Empty);
            string managed = vector ? elementName + "[]" :
                (borrowedArray ? "global::Ankus.PgArrayView<" : "global::Ankus.PgArray<") + elementName + ">";
            string reader = borrowedArray ? "borrowedarray" : "array";
            return new(managed, element.Sql + "[]", reader, reader, string.Empty, nullable, reference: true)
            {
                Element = element,
                IsVector = vector,
            };
        }

        if (type is INamedTypeSymbol { Name: "PgVarlena", Arity: 1 } varlena &&
            varlena.ContainingNamespace.ToDisplayString() == "Ankus")
        {
            if (varlena.TypeArguments[0] is not INamedTypeSymbol payload ||
                CustomTypeDeclaration.Create(payload) is not { NativeLayout: true } layout)
            {
                return null;
            }

            return new("global::Ankus.PgVarlena<" + layout.Managed + ">", layout.Sql, "varlena", "custom", string.Empty, nullable, reference: true)
            {
                CustomType = CustomTypeReference.Create(layout),
            };
        }

        if (type is INamedTypeSymbol mapped && DatumTypeDeclaration.Create(mapped) is { } datum)
        {
            return new(datum.Managed, datum.Sql, "mapped", "mapped", string.Empty, nullable, type.IsReferenceType)
            {
                DatumType = DatumTypeReference.Create(datum),
            };
        }

        if (type is INamedTypeSymbol named && CustomTypeDeclaration.Create(named) is { } custom)
        {
            return new(custom.Managed, custom.Sql, "custom", "custom", string.Empty, nullable, type.IsReferenceType)
            {
                CustomType = CustomTypeReference.Create(custom),
            };
        }

        if (type is INamedTypeSymbol { Name: "PgDatum", Arity: 0 } raw && raw.ContainingNamespace.ToDisplayString() == "Ankus")
        {
            return new("global::Ankus.PgDatum", binding?.Sql ?? "record", "datum", "datum", string.Empty, nullable, reference: true)
            {
                Binding = binding,
            };
        }

        if (type is INamedTypeSymbol { Name: "PgRange", Arity: 1 } range && range.ContainingNamespace.ToDisplayString() == "Ankus")
        {
            FunctionType? subtype = Create(range.TypeArguments[0]);
            string? sql = subtype?.Sql switch
            {
                "integer" => "int4range",
                "bigint" => "int8range",
                "numeric" => "numrange",
                "date" => "daterange",
                "timestamp without time zone" => "tsrange",
                "timestamp with time zone" => "tstzrange",
                "timestamp" => "tsrange",
                "timestamptz" => "tstzrange",
                _ => null,
            };
            if (sql is null || subtype is null || subtype.Nullable)
            {
                return null;
            }

            return new("global::Ankus.PgRange<" + subtype.Managed + ">", sql, sql, sql, string.Empty, nullable, reference: true)
            {
                RangeSubtype = subtype,
            };
        }

        if (type is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
        {
            EnumDeclaration? enumeration = EnumDeclaration.Create(enumType);
            return enumeration is null ? null : new(enumeration.Managed, enumeration.Sql, "enum", "enum", string.Empty, nullable, reference: false)
            {
                Enumeration = enumeration,
            };
        }

        string name = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (name is "global::Ankus.PgCString" or "global::Ankus.PgCStringView")
        {
            return new(name, "cstring", "cstring_bytes", "cstring_bytes", string.Empty, nullable, reference: true);
        }

        if (name is "global::Ankus.PgByteaView" or "global::Ankus.PgTextView")
        {
            string sql = name == "global::Ankus.PgByteaView" ? "bytea" : "text";
            return new(name, sql, sql, sql, string.Empty, nullable, reference: true);
        }

        if (name == "global::Ankus.PgInternal")
        {
            return new(name, "internal", "internal", "internal", string.Empty, nullable, reference: true);
        }

        if (name is "global::Ankus.PgAnyElement" or "global::Ankus.PgAnyArray" or "global::Ankus.PgArrayView")
        {
            string sql = name == "global::Ankus.PgAnyElement" ? "anyelement" : "anyarray";
            return new(name, sql, sql, sql, string.Empty, nullable, reference: true);
        }

        if (name == "global::Ankus.PgHeapTuple")
        {
            return new(name, binding?.Sql ?? "record", "tuple", "tuple", string.Empty, nullable, reference: true)
            {
                Binding = binding,
            };
        }

        string? geometry = name switch
        {
            "global::Ankus.PgPoint" => "point",
            "global::Ankus.PgLineSegment" => "lseg",
            "global::Ankus.PgLine" => "line",
            "global::Ankus.PgBox" => "box",
            "global::Ankus.PgCircle" => "circle",
            "global::Ankus.PgPath" => "path",
            "global::Ankus.PgPolygon" => "polygon",
            _ => null,
        };
        if (geometry is not null)
        {
            return new(name, geometry, geometry, geometry, string.Empty, nullable, reference: geometry is "path" or "polygon");
        }

        if (name is "global::Ankus.PgInet" or "global::System.Net.IPAddress" or "global::Ankus.PgCidr" or "global::System.Net.IPNetwork")
        {
            string sql = name is "global::Ankus.PgInet" or "global::System.Net.IPAddress" ? "inet" : "cidr";
            return new(name, sql, sql, sql, string.Empty, nullable, reference: name == "global::System.Net.IPAddress");
        }

        if (name == "global::Ankus.PgNumeric" || type.SpecialType == SpecialType.System_Decimal)
        {
            return new(type.SpecialType == SpecialType.System_Decimal ? "decimal" : name,
                "numeric", "numeric", "numeric", string.Empty, nullable, reference: false);
        }

        string? temporal = name switch
        {
            "global::Ankus.PgDate" or "global::System.DateOnly" => "date",
            "global::Ankus.PgTime" or "global::System.TimeOnly" => "time",
            "global::Ankus.PgTimeTz" => "timetz",
            "global::Ankus.PgTimestamp" or "global::System.DateTime" => "timestamp",
            "global::Ankus.PgTimestampTz" or "global::System.DateTimeOffset" => "timestamptz",
            "global::Ankus.PgInterval" or "global::System.TimeSpan" => "interval",
            _ => null,
        };
        if (temporal is not null)
        {
            return new(name, temporal, temporal, temporal, string.Empty, nullable, reference: false);
        }

        if (name is "global::System.Guid" or "global::Ankus.PgJson" or "global::Ankus.PgJsonb")
        {
            string sql = name == "global::System.Guid" ? "uuid" : name == "global::Ankus.PgJson" ? "json" : "jsonb";
            return new(name, sql, sql, sql, string.Empty, nullable, reference: false);
        }

        if (name == "global::System.Text.Rune")
        {
            // Rust's char maps to varchar in pgrx; a Rune is one Unicode scalar value carried as character text.
            return new(name, "varchar", "varchar", "varchar", string.Empty, nullable, reference: false);
        }

        if (name == "global::Ankus.PgItemPointer")
        {
            return new(name, "tid", "tid", "tid", string.Empty, nullable, reference: false);
        }

        if (name == "global::Ankus.PgRelation")
        {
            return new(name, "regclass", "OID", "ObjectId", "Integral", nullable, reference: true);
        }

        if (name == "global::Ankus.PgTransactionId")
        {
            return new(name, "xid", "TRANSACTIONID", "TransactionId", "Integral", nullable, reference: false);
        }

        return type.SpecialType switch
        {
            SpecialType.System_Boolean => new("bool", "boolean", "BOOL", "Bool", "Integral", nullable, false),
            SpecialType.System_SByte => new("sbyte", "\"char\"", "CHAR", "Char", "Integral", nullable, false),
            SpecialType.System_Int16 => new("short", "smallint", "INT16", "Int16", "Integral", nullable, false),
            SpecialType.System_Int32 => new("int", "integer", "INT32", "Int32", "Integral", nullable, false),
            SpecialType.System_Int64 => new("long", "bigint", "INT64", "Int64", "Integral", nullable, false),
            SpecialType.System_UInt32 => new("uint", "oid", "OID", "ObjectId", "Integral", nullable, false),
            SpecialType.System_Single => new("float", "real", "FLOAT4", "Float4", "Integral", nullable, false),
            SpecialType.System_Double => new("double", "double precision", "FLOAT8", "Float8", "Integral", nullable, false),
            SpecialType.System_String => new("string", "text", "text", "text", string.Empty, nullable, true),
            SpecialType.System_Void => new("void", "void", string.Empty, string.Empty, string.Empty, false, false),
            _ => null,
        };
    }

    /// <summary>
    /// Gets the built-in scalar OID macro, including the scalar types passed by value.
    /// </summary>
    internal string ScalarOid => IsRelation ? "REGCLASSOID" : Reader switch
    {
        "tuple" => "RECORDOID",
        "INT16" => "INT2OID",
        "INT32" => "INT4OID",
        "INT64" => "INT8OID",
        "OID" => "OIDOID",
        "TRANSACTIONID" => "XIDOID",
        _ => BufferOid,
    };

    /// <summary>
    /// Resolves a parameter with its context-specific SQL type binding.
    /// </summary>
    internal static FunctionType? Create(IParameterSymbol parameter)
        => Create(parameter.Type, SqlTypeReference.Read(parameter.GetAttributes()));

    /// <summary>
    /// Resolves a scalar return with its context-specific SQL type binding.
    /// </summary>
    internal static FunctionType? CreateResult(IMethodSymbol method)
        => Create(method.ReturnType, SqlTypeReference.Read(method.GetReturnTypeAttributes()));

    /// <summary>
    /// Creates a native-only buffer conversion for generated type I/O functions.
    /// </summary>
    internal static FunctionType CreateIoBuffer(string sql, string reader, bool nullable = false)
        => new(string.Empty, sql, reader, reader, string.Empty, nullable, reference: true);
}
