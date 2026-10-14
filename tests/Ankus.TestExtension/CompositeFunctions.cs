[assembly: PgSql("composite-types", """
    CREATE SCHEMA tuple_values;
    CREATE TYPE tuple_values.dog AS (name text, age integer);
    CREATE TYPE tuple_values.other_dog AS (name text, age integer);
    CREATE TYPE tuple_values.empty_row AS ();
    CREATE DOMAIN tuple_values.dog_domain AS tuple_values.dog CHECK ((VALUE).age > 0);
    CREATE DOMAIN tuple_values.required_dog AS tuple_values.dog NOT NULL;
    CREATE TYPE tuple_values.required_holder AS (dog tuple_values.required_dog);
    CREATE TYPE tuple_values.pack AS (leader tuple_values.dog, members tuple_values.dog[], tag text);
    CREATE DOMAIN tuple_values.positive_age AS integer CHECK (VALUE > 0);
    CREATE TYPE tuple_values.checked_row AS (name text, age tuple_values.positive_age);
    CREATE TYPE tuple_values.metadata_row AS (label varchar(3) COLLATE "C", amount numeric(6,2));
    CREATE TABLE tuple_values.relation_row (name text NOT NULL, age integer);
    CREATE TYPE tuple_values.dropped AS (first_value integer, removed text, last_value text);
    ALTER TYPE tuple_values.dropped DROP ATTRIBUTE removed;
    CREATE TYPE tuple_values.ephemeral AS (name text, age integer);
    CREATE TYPE tuple_values.all_types AS (
        flag boolean, small smallint, large bigint, oid_value oid, float_value real, double_value double precision,
        number numeric, day date, clock time, instant timestamptz, span interval, identifier uuid,
        document json, normalized jsonb, address inet, block cidr, location point, extent int4range,
        bytes bytea, words text[], numbers integer[]);
    """, Requires = ["sql-first"])]

namespace Ankus.TestExtension;

/// <summary>
/// Exercises composite identity, owned cells, descriptor metadata, and backend conversion paths.
/// </summary>
[PgSchema("tuple_values", Create = false)]
public static class CompositeFunctions
{
    /// <summary>
    /// Exchanges a named composite through every SPI owner.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    [return: PgCompositeType("dog", Schema = "tuple_values")]
    public static PgHeapTuple? TupleDog([PgCompositeType("dog", Schema = "tuple_values")] PgHeapTuple? value, int mode)
        => Exchange(value, mode);

    /// <summary>
    /// Exchanges a composite containing another tuple and a composite array.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    [return: PgCompositeType("pack", Schema = "tuple_values")]
    public static PgHeapTuple? TuplePack([PgCompositeType("pack", Schema = "tuple_values")] PgHeapTuple? value, int mode)
        => Exchange(value, mode);

    /// <summary>
    /// Exchanges all supported scalar families within a single composite.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    [return: PgCompositeType("all_types", Schema = "tuple_values")]
    public static PgHeapTuple? TuplePrimitives([PgCompositeType("all_types", Schema = "tuple_values")] PgHeapTuple? value, int mode)
        => Exchange(value, mode);

    /// <summary>
    /// Exchanges anonymous records while retaining their registered descriptor.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    public static PgHeapTuple? TupleRecord(PgHeapTuple? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Exchanges arrays without inferring identity from a first nonnull element.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    [return: PgCompositeType("dog", Schema = "tuple_values")]
    public static PgArray<PgHeapTuple?>? TupleArray([PgCompositeType("dog", Schema = "tuple_values")] PgArray<PgHeapTuple?>? value, int mode)
        => Exchange(value, mode);

    /// <summary>
    /// Exchanges a domain whose base datum is a named composite.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    [return: PgCompositeType("dog_domain", Schema = "tuple_values")]
    public static PgHeapTuple? TupleDomainDog([PgCompositeType("dog_domain", Schema = "tuple_values")] PgHeapTuple? value, int mode)
        => Exchange(value, mode);

    /// <summary>
    /// Exchanges arrays whose element identity is a domain over a composite.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    [return: PgCompositeType("dog_domain", Schema = "tuple_values")]
    public static PgArray<PgHeapTuple?>? TupleDomainArray([PgCompositeType("dog_domain", Schema = "tuple_values")] PgArray<PgHeapTuple?>? value, int mode)
        => Exchange(value, mode);

    /// <summary>
    /// Constructs a zero-attribute named composite independently of a SQL input.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    [return: PgCompositeType("empty_row", Schema = "tuple_values")]
    public static PgHeapTuple TupleEmpty() => PgTupleDescriptor.Load("tuple_values.empty_row").CreateTuple();

    /// <summary>
    /// Constructs a named tuple from immutable catalog metadata.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    [return: PgCompositeType("dog", Schema = "tuple_values")]
    public static PgHeapTuple TupleCreate(string? name, int? age)
    {
        PgHeapTuple value = PgTupleDescriptor.Load("tuple_values.dog").CreateTuple();
        value.Set("name", name);
        value.Set(1, age);
        return value;
    }

    /// <summary>
    /// Constructs a registered record with case-sensitive names and typed NULL cells.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    public static PgHeapTuple TupleAnonymous(string? name, int? age)
        => PgHeapTuple.Create(("Name", SpiParameter.Create(name)), ("age", SpiParameter.Create(age)));

    /// <summary>
    /// Exercises guarded anonymous record construction with a field unrepresentable in LATIN1.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    public static PgHeapTuple TupleUntranslatable() => TupleAnonymous("😀", 1);

    /// <summary>
    /// Compares named composite fields while retaining strict whole-row NULL behavior.
    /// </summary>
    [PgFunction(Requires = ["composite-types"], NullInput = PgNullInput.Strict)]
    [PgOperator("@=")]
    public static bool TupleEqual([PgCompositeType("dog", Schema = "tuple_values")] PgHeapTuple left,
        [PgCompositeType("dog", Schema = "tuple_values")] PgHeapTuple right)
        => string.Equals(left.Get<string?>("name"), right.Get<string?>("name"), StringComparison.Ordinal)
            && left.Get<int?>("age") == right.Get<int?>("age");

    /// <summary>
    /// Casts a named composite to its nullable integer field without losing strict whole-row NULL semantics.
    /// </summary>
    [PgFunction(Requires = ["composite-types"], NullInput = PgNullInput.Strict)]
    [PgCast]
    public static int? TupleAge([PgCompositeType("dog", Schema = "tuple_values")] PgHeapTuple value)
        => value.Get<int?>("age");

    /// <summary>
    /// Builds a composite whose fields hold a composite and an array of composites, as pgrx's
    /// <c>test_array_of_composite_type</c> sets them in code.
    /// </summary>
    /// <returns>A pack led by Nami with two members and a NULL member row.</returns>
    [PgFunction(Requires = ["composite-types"])]
    [return: PgCompositeType("pack", Schema = "tuple_values")]
    public static PgHeapTuple TuplePackBuild()
    {
        PgHeapTuple pack = PgTupleDescriptor.Load("tuple_values.pack").CreateTuple();
        pack.Set("leader", TupleCreate("Nami", 3));
        pack.Set("members", PgTupleDescriptor.Load("tuple_values.dog").CreateArray([TupleCreate("Brandy", 1), null, TupleCreate("Ada", 2)]));
        pack.Set("tag", "built");
        return pack;
    }

    /// <summary>
    /// Casts one named composite to another by building the target, as pgrx's <c>castdog_to_castcat</c> does.
    /// </summary>
    /// <param name="value">The source composite.</param>
    /// <returns>The target composite with the source name and a NULL age.</returns>
    [PgFunction(Requires = ["composite-types"])]
    [PgCast]
    [return: PgCompositeType("other_dog", Schema = "tuple_values")]
    public static PgHeapTuple TupleOtherDog([PgCompositeType("dog", Schema = "tuple_values")] PgHeapTuple value)
    {
        PgHeapTuple other = PgTupleDescriptor.Load("tuple_values.other_dog").CreateTuple();
        other.Set("name", value.Get<string?>("name"));
        return other;
    }

    /// <summary>
    /// Reads a composite argument whose SQL default is a row, as pgrx's <c>gets_name_field_default</c> does.
    /// </summary>
    /// <param name="dog">The composite, defaulting to Nami.</param>
    /// <returns>The name field.</returns>
    [PgFunction(Requires = ["composite-types"])]
    public static string? TupleDefaultName(
        [PgCompositeType("dog", Schema = "tuple_values"), PgParameter(Default = "ROW('Nami', 0)::tuple_values.dog")] PgHeapTuple dog)
        => dog.Get<string?>("name");

    /// <summary>
    /// Reads each name from variadic composite arguments, as pgrx's <c>gets_name_field_variadic</c> does.
    /// </summary>
    /// <param name="dogs">The composites, which may include SQL NULL rows.</param>
    /// <returns>The names, with SQL NULL for a NULL row.</returns>
    [PgFunction(Requires = ["composite-types"])]
    public static string?[] TupleVariadicNames([PgCompositeType("dog", Schema = "tuple_values")] params PgHeapTuple?[] dogs)
        => [.. dogs.Select(static dog => dog?.Get<string?>("name"))];

    /// <summary>
    /// Reads names from a defaulted non-variadic composite array, as pgrx's <c>sum_scritches_for_names_array_default</c>
    /// declares one.
    /// </summary>
    /// <param name="dogs">The composites, defaulting to one row named Nami.</param>
    /// <returns>The names.</returns>
    [PgFunction(Requires = ["composite-types"])]
    public static string?[] TupleArrayDefaultNames(
        [PgCompositeType("dog", Schema = "tuple_values"), PgParameter(Default = "ARRAY[ROW('Nami', 0)]::tuple_values.dog[]")]
        PgHeapTuple?[] dogs)
        => [.. dogs.Select(static dog => dog?.Get<string?>("name"))];

    /// <summary>
    /// Describes a defaulted nullable array of nullable composites, as pgrx's exotic signature test declares one.
    /// </summary>
    /// <param name="dogs">The composites, SQL NULL by default.</param>
    /// <returns>"null" for SQL NULL, otherwise the count of rows and of NULL rows.</returns>
    [PgFunction(Requires = ["composite-types"])]
    public static string TupleNullableArrayDefault([PgCompositeType("dog", Schema = "tuple_values")] PgHeapTuple?[]? dogs = null)
        => dogs is null ? "null" : dogs.Length + ":" + dogs.Count(static dog => dog is null);

    /// <summary>
    /// Returns TABLE rows whose column holds an array of composites.
    /// </summary>
    /// <returns>Two rows: one pack with a NULL member and one empty pack.</returns>
    [PgFunction(Requires = ["composite-types"])]
    [return: PgCompositeType("dog", Schema = "tuple_values", Column = "pack")]
    public static IEnumerable<(int Id, PgHeapTuple?[] Pack)> TupleArrayTable()
    {
        yield return (1, [TupleCreate("Nami", 3), null]);
        yield return (2, []);
    }

    /// <summary>
    /// Reads names from a defaulted variadic composite argument, as pgrx's <c>gets_name_field_default_variadic</c> does.
    /// </summary>
    /// <param name="dogs">The composites, defaulting to one row named Nami.</param>
    /// <returns>The names.</returns>
    [PgFunction(Requires = ["composite-types"])]
    public static string?[] TupleDefaultVariadicNames(
        [PgCompositeType("dog", Schema = "tuple_values"), PgParameter(Default = "ARRAY[ROW('Nami', 0)]::tuple_values.dog[]")]
        params PgHeapTuple?[] dogs)
        => [.. dogs.Select(static dog => dog?.Get<string?>("name"))];

    /// <summary>
    /// Constructs composite arrays with explicit identity and nondefault dimensions.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    [return: PgCompositeType("dog", Schema = "tuple_values")]
    public static PgArray<PgHeapTuple?> TupleArrayCreate(int scenario)
    {
        PgTupleDescriptor descriptor = PgTupleDescriptor.Load("tuple_values.dog");
        return scenario switch
        {
            0 => descriptor.CreateArray([]),
            1 => descriptor.CreateArray([null, TupleCreate("Ada", 3)]),
            2 => descriptor.CreateArray([null, null]),
            3 => descriptor.CreateArray([TupleCreate("Ada", 3), null, descriptor.CreateTuple(), TupleCreate("Bo", 4)], [2, 2], [-1, 3]),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
    }

    /// <summary>
    /// Constructs ordinary managed vectors whose annotated SQL element type supplies named identity.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    [return: PgCompositeType("dog", Schema = "tuple_values")]
    public static PgHeapTuple?[]? TupleVectorCreate(int scenario)
        => scenario switch
        {
            0 => [],
            1 => [null, null],
            2 => [null, TupleCreate("Ada", 3)],
            3 => [PgTupleDescriptor.Load("tuple_values.other_dog").CreateTuple()],
            4 => null,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

    /// <summary>
    /// Constructs shaped arrays with ordinary record element metadata and an annotated named SQL target.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    [return: PgCompositeType("dog", Schema = "tuple_values")]
    public static PgArray<PgHeapTuple?>? TuplePlainArrayCreate(int scenario)
    {
        PgHeapTuple?[]? values = TupleVectorCreate(scenario);
        return values is null ? null : values.Length == 0 ? new PgArray<PgHeapTuple?>([]) : new PgArray<PgHeapTuple?>(values, [values.Length], [-2]);
    }

    /// <summary>
    /// Reads and writes an ordinary managed vector of named composite cells.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    [return: PgCompositeType("dog", Schema = "tuple_values")]
    public static PgHeapTuple?[]? TupleVector([PgCompositeType("dog", Schema = "tuple_values")] PgHeapTuple?[]? value)
        => value;

    /// <summary>
    /// Exposes physical descriptor metadata independently of tuple output serialization.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    public static IEnumerable<(int Ordinal, string Name, uint TypeOid, uint BaseTypeOid, int TypeModifier,
        uint CollationOid, bool IsDropped, bool IsNotNull, bool IsComposite)> TupleAttributes(string typeName, bool byOid)
    {
        PgTupleDescriptor descriptor = byOid
            ? PgTupleDescriptor.Load(Spi.ExecuteScalar<uint>("SELECT $1::regtype::oid", SpiParameter.Create(typeName)))
            : PgTupleDescriptor.Load(typeName);
        for (int ordinal = 0; ordinal < descriptor.Attributes.Count; ordinal++)
        {
            PgTupleAttributeInfo attribute = descriptor.Attributes[ordinal];
            yield return (ordinal, attribute.Name, attribute.TypeOid, attribute.BaseTypeOid, attribute.TypeModifier,
                attribute.CollationOid, attribute.IsDropped, attribute.IsNotNull, attribute.IsComposite);
        }
    }

    /// <summary>
    /// Exposes tuple header identity and physical count after later SPI allocations.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    public static long[] TupleIdentity(PgHeapTuple value)
    {
        Spi.Execute("SELECT repeat('replacement', 10000)");
        return [value.Descriptor.TypeOid, value.Descriptor.TypeModifier, value.Count];
    }

    /// <summary>
    /// Exposes a descriptor's declared domain identity independently of its base composite identity.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    public static uint[] TupleDescriptorIdentity(string name)
    {
        PgTupleDescriptor descriptor = PgTupleDescriptor.Load(name);
        return [descriptor.TypeOid, descriptor.BaseTypeOid, SpiParameter.Create(descriptor.CreateTuple(), descriptor).TypeOid];
    }

    /// <summary>
    /// Demonstrates that replacing clone slots leaves the original tuple unchanged.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    public static string?[] TupleClone([PgCompositeType("dog", Schema = "tuple_values")] PgHeapTuple value)
    {
        PgHeapTuple copy = value.Clone();
        copy.Set(0, "Changed");
        copy.Set("age", 99);
        return [value.Get<string>("name"), value.Get<int>(1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            copy.Get<string>(0), copy.Get<int>("age").ToString(System.Globalization.CultureInfo.InvariantCulture)];
    }

    /// <summary>
    /// Loads a row descriptor by name, failing as PostgreSQL does for a type that does not exist.
    /// </summary>
    /// <param name="name">The type name.</param>
    /// <returns>The loaded type's attribute count.</returns>
    [PgFunction(Requires = ["composite-types"])]
    public static int TupleLoadByName(string name) => PgTupleDescriptor.Load(name).Attributes.Count;

    /// <summary>
    /// Returns the precise exception type from rejected accesses while checking the tuple remains intact.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    public static string TupleAccessFailure([PgCompositeType("dog", Schema = "tuple_values")] PgHeapTuple value, int scenario)
    {
        try
        {
            switch (scenario)
            {
                case 0:
                    _ = value.Get<int>("name");
                    break;
                case 1:
                    value.Set("age", "wrong");
                    break;
                case 2:
                    _ = value.Get<string>("Name");
                    break;
                case 3:
                    _ = value.Get<int>(-1);
                    break;
                case 4:
                    value.Set(value.Count, 1);
                    break;
                case 5:
                    _ = PgTupleDescriptor.Load("tuple_values.pack").CreateArray([value]);
                    break;
                case 6:
                    PgTupleDescriptor.Load("tuple_values.pack").CreateTuple().Set("leader", PgTupleDescriptor.Load("tuple_values.other_dog").CreateTuple());
                    break;
                case 7:
                    value.Set("missing", "x");
                    break;
                case 8:
                    _ = PgTupleDescriptor.Load("tuple_values.dog").CreateTuple().Get<int?>("name");
                    break;
                case 9:
                    _ = Spi.ExecuteScalar<PgHeapTuple>("SELECT 1");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario));
            }
        }
        catch (Exception error) when (error is InvalidCastException or ArgumentException)
        {
            return $"{error.GetType().Name}:{value.Get<string>(0)}:{value.Get<int>(1)}";
        }

        return "no error";
    }

    /// <summary>
    /// Reads physical dropped slots and tests that neither name nor ordinal permits mutation.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    public static string TupleDropped([PgCompositeType("dropped", Schema = "tuple_values")] PgHeapTuple value)
    {
        string named;
        string ordinal;
        try
        {
            value.Set("removed", "invalid");
            named = "no error";
        }
        catch (ArgumentException error)
        {
            named = error.GetType().Name;
        }

        try
        {
            value.Set(1, "invalid");
            ordinal = "no error";
        }
        catch (InvalidOperationException error)
        {
            ordinal = error.GetType().Name;
        }

        return $"{value.Count}:{value.Get<int>(0)}:{value[1] is null}:{value.Get<string>(2)}:{named}:{ordinal}";
    }

    /// <summary>
    /// Keeps nested, potentially toasted values after their SPI owner and later garbage collections.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    [return: PgCompositeType("pack", Schema = "tuple_values")]
    public static PgHeapTuple TupleRetained()
    {
        PgHeapTuple value = Spi.Connect(session => session.ExecuteScalar<PgHeapTuple>("""
            SELECT ROW(ROW(repeat('hé😀',20000),8)::tuple_values.dog,
                ARRAY[NULL,ROW('nested',9)::tuple_values.dog],repeat('tag',20000))::tuple_values.pack
            """));
        Spi.Execute("SELECT array_agg(repeat('replacement',1000)) FROM generate_series(1,100)");
        GarbageCollection.Collect();
        return value;
    }

    /// <summary>
    /// Copies a physical table row whose nested fields can reference relation-owned TOAST values.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    [return: PgCompositeType("pack", Schema = "tuple_values")]
    public static PgHeapTuple TupleStored()
    {
        PgHeapTuple value = Spi.ExecuteScalar<PgHeapTuple>("SELECT value FROM tuple_toast");
        Spi.Execute("DELETE FROM tuple_toast; SELECT repeat('overwritten',10000)");
        GarbageCollection.Collect();
        return value;
    }

    /// <summary>
    /// Creates a standalone table-row composite without applying relation insertion constraints.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    [return: PgCompositeType("relation_row", Schema = "tuple_values")]
    public static PgHeapTuple TupleRelationRow() => PgTupleDescriptor.Load("tuple_values.relation_row").CreateTuple();

    /// <summary>
    /// Returns a descriptor retained across incompatible catalog changes.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    public static PgHeapTuple TupleStale(int scenario)
    {
        PgHeapTuple value = PgTupleDescriptor.Load("tuple_values.ephemeral").CreateTuple();
        value.Set("name", "before");
        value.Set("age", 7);
        Spi.Execute(scenario switch
        {
            0 => "ALTER TYPE tuple_values.ephemeral ADD ATTRIBUTE extra integer",
            1 => "ALTER TYPE tuple_values.ephemeral DROP ATTRIBUTE age",
            2 => "ALTER TYPE tuple_values.ephemeral ALTER ATTRIBUTE age TYPE bigint",
            3 => "ALTER EXTENSION ankus_test DROP TYPE tuple_values.ephemeral; DROP TYPE tuple_values.ephemeral; CREATE TYPE tuple_values.ephemeral AS (name text, age integer)",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        });
        return value;
    }

    /// <summary>
    /// Constructs domain fields without bypassing native domain checks.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    [return: PgCompositeType("checked_row", Schema = "tuple_values")]
    public static PgHeapTuple TupleDomain(int? age)
    {
        PgHeapTuple value = PgTupleDescriptor.Load("tuple_values.checked_row").CreateTuple();
        value.Set("name", "domain");
        value.Set("age", age);
        return value;
    }

    /// <summary>
    /// Constructs a domain composite with valid or invalid base fields for native domain validation.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    [return: PgCompositeType("dog_domain", Schema = "tuple_values")]
    public static PgHeapTuple TupleDomainCreate(int age)
    {
        PgHeapTuple value = PgTupleDescriptor.Load("tuple_values.dog_domain").CreateTuple();
        value.Set("name", "domain dog");
        value.Set("age", age);
        return value;
    }

    /// <summary>
    /// Creates domain-element arrays using a domain descriptor and base-composite cells.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    [return: PgCompositeType("dog_domain", Schema = "tuple_values")]
    public static PgArray<PgHeapTuple?> TupleDomainArrayCreate(int age)
        => PgTupleDescriptor.Load("tuple_values.dog_domain").CreateArray([null, TupleCreate("domain dog", age)]);

    /// <summary>
    /// Distinguishes a NULL domain datum from a nonnull tuple containing only NULL fields.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    [return: PgCompositeType("required_dog", Schema = "tuple_values")]
    public static PgHeapTuple? TupleRequired(bool wholeNull)
        => wholeNull ? null : PgTupleDescriptor.Load("tuple_values.required_dog").CreateTuple();

    /// <summary>
    /// Returns a NULL field of a NOT NULL composite domain for guarded native rejection.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    [return: PgCompositeType("required_holder", Schema = "tuple_values")]
    public static PgHeapTuple TupleRequiredField() => PgTupleDescriptor.Load("tuple_values.required_holder").CreateTuple();

    /// <summary>
    /// Returns a NULL array element of a NOT NULL composite domain for guarded native rejection.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    [return: PgCompositeType("required_dog", Schema = "tuple_values")]
    public static PgArray<PgHeapTuple?> TupleRequiredArray()
        => PgTupleDescriptor.Load("tuple_values.required_dog").CreateArray([null]);

    /// <summary>
    /// Constructs typmod-constrained fields using their ordinary managed scalar types.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    [return: PgCompositeType("metadata_row", Schema = "tuple_values")]
    public static PgHeapTuple TupleTypmod(string label, PgNumeric amount)
    {
        PgHeapTuple value = PgTupleDescriptor.Load("tuple_values.metadata_row").CreateTuple();
        value.Set("label", label);
        value.Set("amount", amount);
        return value;
    }

    /// <summary>
    /// Recovers repeatedly from domain validation failures while preserving prior writes and a named prepared plan.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    public static string TupleRecover()
        => Spi.Connect(session =>
        {
            session.Execute("CREATE TEMP TABLE tuple_writes(value int); INSERT INTO tuple_writes VALUES(1)");
            PgHeapTuple retained = TupleCreate("retained", 41);
            SpiParameter parameter = SpiParameter.Create(retained);
            using SpiPreparedStatement plan = session.PrepareWithTypeOids("SELECT $1", parameter.TypeOid);
            int failures = 0;
            for (int index = 0; index < 20; index++)
            {
                try
                {
                    session.Execute("SELECT $1", SpiParameter.Create(TupleDomain(-1)));
                }
                catch (PgException error) when (error.SqlState == "23514")
                {
                    failures++;
                }
            }

            PgHeapTuple echoed = plan.ExecuteScalar<PgHeapTuple>(parameter);
            return $"{failures}:{session.ExecuteScalar<long>("SELECT count(*) FROM tuple_writes")}:{echoed.Get<string>("name")}:{echoed.Get<int>("age")}";
        });

    /// <summary>
    /// Streams a NULL row, an all-null tuple, and a populated named tuple.
    /// </summary>
    [PgFunction(Requires = ["composite-types"], SetMode = PgSetMode.ValuePerCall)]
    [return: PgCompositeType("dog", Schema = "tuple_values")]
    public static IEnumerable<PgHeapTuple?> TupleSet()
    {
        yield return null;
        yield return PgTupleDescriptor.Load("tuple_values.dog").CreateTuple();
        yield return TupleCreate("set", 12);
    }

    /// <summary>
    /// Streams many named composites built from one descriptor loaded before the first row, as pgrx's
    /// <c>generate_lots_of_dogs</c> does.
    /// </summary>
    /// <param name="count">The number of rows.</param>
    /// <returns>Rows named by their one-based position, with that position as the age.</returns>
    [PgFunction(Requires = ["composite-types"], SetMode = PgSetMode.ValuePerCall)]
    [return: PgCompositeType("dog", Schema = "tuple_values")]
    public static IEnumerable<PgHeapTuple> TupleManyDogs(int count)
    {
        PgTupleDescriptor descriptor = PgTupleDescriptor.Load("tuple_values.dog");
        for (int index = 1; index <= count; index++)
        {
            PgHeapTuple dog = descriptor.CreateTuple();
            dog.Set("name", "dog " + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            dog.Set("age", index);
            yield return dog;
        }
    }

    /// <summary>
    /// Reads every row of a large composite set through SPI, as pgrx's <c>test_tuple_desc_clone</c> does.
    /// </summary>
    /// <param name="count">The number of rows to generate.</param>
    /// <returns>The row count, distinct names and the sum of ages that SPI read.</returns>
    [PgFunction(Requires = ["composite-types"])]
    public static string TupleManyDogsThroughSpi(int count)
    {
        SpiResult rows = Spi.Query(Spi.Sql($"SELECT d FROM tuple_values.tuple_many_dogs({count}) d"));
        var names = new HashSet<string>(StringComparer.Ordinal);
        long ages = 0;
        foreach (PgHeapTuple dog in rows.Select(static row => row.Get<PgHeapTuple>(0)))
        {
            names.Add(dog.Get<string>("name"));
            ages += dog.Get<int>("age");
        }

        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{rows.Count}|{names.Count}|{ages}");
    }

    /// <summary>
    /// Materializes the same named composite rows through PostgreSQL's tuplestore.
    /// </summary>
    [PgFunction(Requires = ["composite-types"], SetMode = PgSetMode.Materialize)]
    [return: PgCompositeType("dog", Schema = "tuple_values")]
    public static IEnumerable<PgHeapTuple?> TupleSetMaterialized() => TupleSet();

    /// <summary>
    /// Streams anonymous records whose descriptor is supplied by the SQL caller.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    public static IEnumerable<PgHeapTuple?> TupleAnonymousSet()
    {
        yield return TupleAnonymous("first", 1);
        yield return TupleAnonymous(null, null);
    }

    /// <summary>
    /// Materializes anonymous rows only when the caller supplies the required tuple descriptor.
    /// </summary>
    [PgFunction(Requires = ["composite-types"], SetMode = PgSetMode.Materialize)]
    public static IEnumerable<PgHeapTuple?> TupleAnonymousSetMaterialized() => TupleAnonymousSet();

    /// <summary>
    /// Returns named composite fields inside a TABLE result.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    [return: PgCompositeType("dog", Schema = "tuple_values", Column = "dog")]
    [return: PgCompositeType("other_dog", Schema = "tuple_values", Column = "other")]
    public static IEnumerable<(int Id, PgHeapTuple? Dog, PgHeapTuple? Other)> TupleTable()
    {
        yield return (1, TupleCreate("table", 5), PgTupleDescriptor.Load("tuple_values.other_dog").CreateTuple());
        yield return (2, null, null);
    }

    /// <summary>
    /// Returns a one-column TABLE whose cell is a named composite.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    [return: PgColumnNames("dog")]
    [return: PgCompositeType("dog", Schema = "tuple_values")]
    public static IEnumerable<PgHeapTuple?> TupleSingleTable() => TupleSet();

    /// <summary>
    /// Exchanges owned tuple values through direct, query, plan, session, cursor, and edited-row paths.
    /// </summary>
    private static T Exchange<T>(T value, int mode)
    {
        const string sql = "SELECT $1";
        SpiParameter parameter = SpiParameter.Create(value);
        switch (mode)
        {
            case 0:
                return value;
            case 1:
                return Spi.ExecuteScalar<T>(sql, parameter);
            case 2:
                using (SpiPreparedStatement plan = Spi.PrepareWithTypeOids(sql, parameter.TypeOid))
                {
                    return plan.ExecuteScalar<T>(parameter);
                }

            case 3:
                return Spi.Connect(session => session.ExecuteScalar<T>(sql, parameter));
            case 4:
                using (SpiCursor cursor = Spi.OpenCursor(sql, parameter))
                {
                    SpiRow row = cursor.Fetch(1)[0];
                    cursor.Fetch(1);
                    return row.Get<T>(0);
                }

            case 5:
                return Spi.Connect(session =>
                {
                    using SpiPreparedStatement plan = session.PrepareWithTypeOids(sql, parameter.TypeOid);
                    using SpiCursor cursor = plan.OpenCursor(parameter);
                    return cursor.Fetch(1)[0].Get<T>(0);
                });
            case 6:
                using (SpiPreparedStatement plan = Spi.Connect(session => session.PrepareWithTypeOids(sql, parameter.TypeOid).Keep()))
                {
                    return plan.ExecuteScalar<T>(parameter);
                }

            case 7:
                SpiRow edited = Spi.Query("SELECT 42 AS value")[0];
                edited.Set("value", value);
                return Spi.ExecuteScalar<T>(sql, SpiParameter.Create(edited.Get<T>(0)));
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }
}
