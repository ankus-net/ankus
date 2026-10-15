---
title: Operators and casts
description: Declare PostgreSQL operators and casts with C# methods, operators and conversions.
---

`PgOperator` and `PgCast` generate a backing SQL function plus an operator or cast
that calls it. Both work on the same supported types as `PgFunction`, including
generated enums and arrays. They use the same NULL handling, Native AOT conversion,
and managed exception boundary.

## Operators

Apply `PgOperator` to a static method with two parameters for a binary operator,
or one parameter for a prefix operator. It also accepts C# operator declarations:

```csharp
[PgOperator("@+")]
[PgFunction(Volatility = PgVolatility.Immutable,
    ParallelSafety = PgParallelSafety.Safe)]
public static int CheckedAdd(int left, int right) => checked(left + right);

[PgOperator("@-")]
public static int Negate(int value) => checked(-value);
```

```sql
SELECT 20 @+ 22;  -- 42
SELECT @- 7;     -- -7
```

`PgFunction` is optional. Use it to set the backing function's SQL name, schema,
volatility, parallel mode, strictness, or other [execution options](/function-declarations/).
The operator belongs to that function's schema, including inherited `PgSchema`
declarations. Without a fixed schema, both belong to the extension's installation
schema and move together during relocation.

To call an operator in a specific schema, use PostgreSQL's `OPERATOR` syntax:

```sql
SELECT 20 OPERATOR(calculations.@+) 22;
SELECT OPERATOR(calculations.@-) 7;
```

Names contain PostgreSQL operator punctuation and occupy at most 63 bytes. They
cannot contain comment starts (`--` or `/*`). A name longer than one character
can end in `+` or `-` only if it contains a nonstandard operator character such
as `@` or `?`. PostgreSQL treats `!=` as `<>`; Ankus uses the same normalization.
The exact token `=>` is reserved. Operators cannot be variadic or return `void`.

Nullable parameters follow the normal function policy. An operator with required
parameters is strict by default; nullable parameters let the method handle NULL.
Postfix unary operators are not generated.

### C# operator declarations

Put the attributes directly on the operator when your managed type already
defines the operation:

```csharp
[PgType]
public readonly record struct Amount(int Value)
{
    [PgOperator("@+")]
    [PgFunction(Name = "amount_add", Volatility = PgVolatility.Immutable)]
    public static Amount operator +(Amount left, Amount right)
        => new(checked(left.Value + right.Value));
}
```

The SQL backing function calls that exact declaration. A checked operator such
as `operator checked +` keeps its checked implementation; `operator true` and
`operator false` also keep their separate implementations. The operator's C#
token and the PostgreSQL token are independent: `PgOperator` chooses the SQL
token. SQL arguments and results still need supported Ankus type mappings.

Without `PgFunction.Name`, the backing name follows the metadata operation, such
as `op_addition`, `op_checked_addition` or `op_unary_negation`. Use an explicit
name for a public SQL API. The attribute does not add a C# operator to an ordinary
method or change C# overload resolution in your extension code.

Use [`PgQualifiedNameBuilder`](/catalog-lookups/#qualified-operator-names) to
resolve an existing operator's OID from exact name components and argument types.
Lookup follows the current search path and schema permissions without coercing types.

## Planner options

```csharp
[PgOperator("===", Commutator = "===", Negator = "!==",
    RestrictionEstimator = "pg_catalog.eqsel",
    JoinEstimator = "pg_catalog.eqjoinsel", Hashes = true, Merges = true)]
[PgFunction(Volatility = PgVolatility.Immutable,
    ParallelSafety = PgParallelSafety.Safe)]
public static bool Equal(int left, int right) => left == right;
```

| Option | PostgreSQL contract |
| --- | --- |
| `Commutator` | Names the operator with reversed operands. Only binary operators support this. |
| `Negator` | Names the boolean complement with the same operand types. |
| `RestrictionEstimator` | Names a restriction selectivity function (`RESTRICT`). Requires a boolean result. |
| `JoinEstimator` | Names a join selectivity function (`JOIN`). Requires a binary boolean operator. |
| `Hashes`, `Merges` | Declare hash/merge join support. Require a binary boolean operator. |

Unqualified commutator and negator names use the operator's schema. A name such
as `comparison.!==` explicitly selects another schema. Estimator names can be
unqualified or prefixed with one schema. PostgreSQL validates referenced routines
at installation.

Commutator/negator pairs may refer to each other: PostgreSQL creates a temporary
shell entry and fills it when the companion operator is declared. A self-commutator
is supported; an operator cannot be its own negator. These links are not
installation dependency cycles.

Planner options are semantic promises. A commutator must actually reverse the
operands, a negator must complement the result, and equality/hash/sort behavior
must agree. `Hashes` and `Merges` do not create operator classes or support
functions. Use the generated type operators below or declare compatible operator
families using [custom SQL](/custom-sql/) when PostgreSQL needs them for joins or indexes.

## Generated type operators

Use `[PgEquality]`, `[PgOrdering]`, and `[PgHashing]` on a `[PgType]`, `[PgEnum]`,
or readable `[PgDatumType]` declaration to expose its managed value semantics
to PostgreSQL:

```csharp
[PgType]
[PgEquality]
[PgOrdering]
[PgHashing]
public sealed record ProductKey(string Value) : IComparable<ProductKey>, IPgHashable
{
    public int CompareTo(ProductKey? other)
        => other is null ? 1 : string.CompareOrdinal(Value, other.Value);

    public int GetPostgresHashCode() => PgHash.Compute(Value);
}
```

The record supplies `IEquatable<ProductKey>`. Its equality is based on its string
value, and its explicit ordering and database hash use the same value.

```sql
CREATE TABLE products (key product_key);
CREATE INDEX products_ordered ON products USING btree (key);
CREATE INDEX products_hashed ON products USING hash (key);
SELECT key FROM products ORDER BY key;
```

| Attribute | Required managed contract | Generated PostgreSQL objects |
| --- | --- | --- |
| `PgEquality` | `IEquatable<T>` for the exact declared type | `=` and `<>`, backed by `<type>_eq` and `<type>_ne` |
| `PgOrdering` | `IEquatable<T>` and `IComparable<T>` | `<`, `>`, `<=` and `>=`, backed by `<type>_lt`, `<type>_gt`, `<type>_le` and `<type>_ge`, plus `<type>_cmp` and a default B-tree family/class `<type>_btree_ops` |
| `PgHashing` | `IEquatable<T>` and `IPgHashable` | `<type>_hash` and a default hash family/class `<type>_hash_ops` |

Each attribute is independent. Ordering and hashing require a compatible boolean
`=` operator for the same SQL type in the same schema. `PgEquality` generates it;
an explicitly declared `[PgOperator("=")]` can supply it instead. Manual equality
must declare compatible planner options when used for joins. Ankus diagnoses a
missing equality declaration during generation.

Explicit interface implementations and inherited implementations are supported.
The generated callbacks make statically bound calls compatible with Native AOT.
They use the existing conversion, cleanup and exception boundary; a managed
exception unwinds before PostgreSQL raises its error. All generated support
functions are `IMMUTABLE`, `PARALLEL SAFE`, and `STRICT`. SQL NULL bypasses the
managed implementation, including for reference types.

Equality must be an equivalence relation, comparison must define a total order,
and comparison must return zero exactly when equality is true. Any negative or
positive `CompareTo` result works, including the minimum and maximum `int`.
These contracts apply to logical values, independently of serialized storage.
The attributes support generated CBOR, custom text, explicit codecs, tagged class
hierarchies and native struct storage. Native-layout comparisons receive copied
`T` values and preserve existing `PgVarlena<T>` aliases.

### Manual datum mappings

A [`PgDatumType` mapping](/raw-values/#reusable-scalar-mappings) can use the same
attributes. Its `IPgDatumReader<T>` decodes operands for the exact declared CLR
type before the comparison or hash runs. No writer is required: generated
helpers return ordinary SQL `boolean` or `integer`. SQL NULL bypasses both the
reader and its lazy converter factory. The reader must return detached managed
values, just as it does for other generated function inputs.

The reader and the value methods share the immutable, parallel-safe contract.
Logical equality, ordering and hashing may differ from the raw stored bytes,
but must agree with one another and remain stable across backends and extension
versions. Changing how a reader interprets indexed values can require rebuilding
indexes even when the comparison methods themselves have not changed.

For an owned mapping, its `PgSqlTypeProvider` must complete the SQL type before
the generated support functions and families. Ordinary input/output functions
can follow a shell type; a completed family cannot precede its type provider.
Multiple CLR wrappers may share one SQL type, but conflicting generated SQL
signatures are rejected rather than choosing one wrapper's reader.
A generic datum mapping emits helpers only for closed types that a function
signature, managed type provider or closed mapping declaration selects. Closed
declarations with their own SQL identities generate independent families. Closed
types sharing one default declaration share its SQL name, so giving two of them
the same generated attributes reports `ANKUS510` or `ANKUS511`. An open converter
is inferred and checked against its constraints for each selected type before any
helper is emitted; its closed reader supplies the operands.

External mappings can also opt in. Helpers, operators and families use the mapped
SQL type's schema and belong to the extension; the external type remains
externally owned. These fixed-schema objects prevent extension relocation.
PostgreSQL checks schema privileges and existing objects during installation.
It permits only one default operator class per type and index method, so adding
a second default class for a built-in type fails. Family SQL controls below can
select a nondefault class or omit it; generated functions and operators remain.

PostgreSQL resolves a domain's default index class through its base type. A
generated class declared for a domain does not make ordinary domain indexes
select that class automatically. A manual base type with these families can
use the generated defaults directly.

### Stable database hashes

PostgreSQL persists hash results in indexes. Implement
`IPgHashable.GetPostgresHashCode()` so equal values produce equal hashes and
the result stays identical across backend processes, platforms and extension
versions. Do not forward to ordinary `object.GetHashCode()`, string or record
hashes, or `System.HashCode`: those APIs do not promise that stability.

`PgHash.Compute` uses the SeaHash v4 byte-buffer algorithm with pgrx's fixed seeds
and returns the low 32 bits as a signed integer. Its overloads accept exact bytes,
strict UTF-8 text without a BOM, or an unsigned integer encoded as eight
little-endian bytes. Text retains embedded zero characters and rejects unpaired
UTF-16 surrogates. The helper does not normalize text or reproduce Rust's
type-specific `Hash` encoding.

Choose a canonical equality key before hashing. For example, if equality ignores
a stored description, exclude that description from the key. If equality treats
different decimal scales, signed floating-point zeroes, or letter cases as equal,
normalize those distinctions consistently for both hashing and comparison.
Keep the normalization contract stable too: culture-dependent comparisons and
changing Unicode tables can change persisted keys. The repository's
`samples/Ankus.Examples.CustomTypes/OrderedKey.cs` demonstrates fixed ASCII case
folding while retaining the original spelling in storage.

Unequal values may have the same hash; PostgreSQL rechecks equality to distinguish
collisions. Changing equality, ordering, normalization or hashing for indexed
values requires a deliberate data/index migration and rebuilding affected indexes.

### Enums, schemas and dependencies

Enums declared with `[PgEnum]`, `[PgType]`, or `[PgDatumType]` can opt in. They use underlying numeric
equality and ordering, and hash the numeric value converted to `ulong` in an
unchecked context. A `[PgEnum]` without these attributes keeps PostgreSQL's label
declaration order. **Adding `PgOrdering` explicitly selects numeric order for its
concrete default B-tree class**, which can differ from label declaration order.
Use the generated operator's schema in `search_path`, or call it with
`OPERATOR(schema.<)` (and the corresponding token for other comparisons).
Qualifying only the operand type does not control operator lookup; an unqualified
enum comparison can otherwise resolve to PostgreSQL's built-in enum operator.

Generated functions, operators, families and classes belong to the type's schema.
Names use the SQL type name; when a suffix would exceed PostgreSQL's 63-byte
identifier limit, Ankus uses a name hashed from the extension assembly's name and
the managed type, which stays the same across assembly versions. Installation dependencies
put completed types before support functions, operators and classes. Each attribute
has `Id` and `Requires`: an equality ID identifies both equality operators, and
an ordering or hashing ID identifies the completed family and class. Custom SQL
can require these IDs when creating an index or another dependent object.

`PgOrdering.Sql` and `PgHashing.Sql` replace only their operator family/class
statements. `GenerateSql = false` omits those statements while retaining
comparison/hash functions, relational operators and the equality dependency.
Use `@COMPARISON_FUNCTION_SQL@` or `@HASH_FUNCTION_SQL@` directly in replacement
SQL for the exact quoted support-function identifier, including long-name
fallbacks. Replacement classes can choose their names and `DEFAULT` status.
`PgEquality` has no SQL override controls. See
[index-family SQL controls](/custom-sql/#replace-index-families).

Generated-operator diagnostics distinguish the required correction:

| Diagnostic | Required correction |
| --- | --- |
| ANKUS413 | Add a valid, accessible `PgType`, `PgEnum`, or `PgDatumType` declaration. |
| ANKUS414 | Add an exact datum reader to the mapped type. |
| ANKUS415 | Implement `IEquatable<T>` for the exact managed type. |
| ANKUS416 | Implement `IComparable<T>` for the exact managed type. |
| ANKUS417 | Implement `IPgHashable` with an equality-compatible hash. |
| ANKUS418 | Add `PgEquality` or an exact same-schema boolean `=` operator. |
| ANKUS510 | A generated support function's signature is already declared. Rename or remove the conflicting function, or remove the reported `PgEquality`, `PgOrdering` or `PgHashing` attribute. |
| ANKUS511 | A generated comparison operator's signature is already declared. Remove the conflicting operator or the reported generated-operator attribute. |

Each `ANKUS510` and `ANKUS511` error names the duplicated signature and points
at the attribute that generates it. Invalid `Id` and `Requires` values,
`[PgBefore]` references and dependency cycles report the
[dependency graph diagnostics](/custom-sql/#dependency-graph-diagnostics);
`Sql` and `GenerateSql` errors report the
[SQL replacement diagnostics](/custom-sql/#sql-replacement-diagnostics).
Arbitrary custom SQL is validated by PostgreSQL during
installation. Generated objects are extension members and move with a
relocatable extension.

## Casts

Apply `PgCast` to a static conversion method:

```csharp
[PgEnum]
public enum Priority { Low = 10, Normal = 20, High = 30 }

public static class PriorityFunctions
{
    [PgCast]
    [PgFunction(Volatility = PgVolatility.Immutable)]
    public static int Score(Priority value) => (int)value;
}
```

```sql
SELECT 'High'::priority::integer; -- 30
```

The first parameter determines the source SQL type, and the return type determines
the target. C# enum numeric values are converted by the method; PostgreSQL enum
OIDs never become the integer result accidentally.

| Context | Declaration | Where PostgreSQL may apply it |
| --- | --- | --- |
| Explicit | `[PgCast]` or `[PgCast(PgCastContext.Explicit)]` | `CAST(value AS type)` and `value::type` |
| Assignment | `[PgCast(PgCastContext.Assignment)]` | Explicit conversions and assignments to columns |
| Implicit | `[PgCast(PgCastContext.Implicit)]` | Explicit conversions, assignments, and expression/function resolution |

Use implicit casts when the conversion is unambiguous and preserves the intended
meaning. PostgreSQL chooses among all casts and overloads in the database; the
attribute does not change its resolution rules.

PostgreSQL can also supply a second `int` parameter containing the target type
modifier and a third `bool` parameter indicating whether the conversion was
explicit. Both must be non-nullable. Type modifiers are PostgreSQL's encoded
values; `-1` means no modifier was specified. A one-parameter cast must connect
different SQL types. Two- or three-parameter methods can implement same-type
length coercion where PostgreSQL supports it.

Nullable first parameters and results use the usual function NULL policy.
Variadic casts and `void` results are rejected. CLR aliases such as `decimal` and
`PgNumeric` share a SQL type, so they do not define distinct cast signatures.

`PgCast` also accepts C# conversion declarations, including `implicit`,
`explicit` and checked explicit conversions. Choose the PostgreSQL context with
`PgCastContext`; a C# implicit conversion does not by itself make a SQL cast
implicit. For example, inside the `Amount` type above:

```csharp
[PgCast(PgCastContext.Explicit)]
[PgFunction(Name = "amount_integer")]
public static explicit operator int(Amount value) => value.Value;
```

Conversions bind both the parameter and result types exactly. If several C#
conversions take the same input but return different types, give their backing
functions different `PgFunction.Name` values: PostgreSQL cannot overload a
function by its result type. An unattributed conversion creates no SQL cast.
Checked and unchecked conversions for the same source/target pair need distinct
backing names, and only one may declare that PostgreSQL cast.

## Installation dependencies and ownership

Operators and casts are separate SQL graph entries, ordered after their backing
functions and argument/result type declarations. Their `Id` and `Requires`
properties refer to the operator or cast; `PgFunction.Id` refers to the function.
For example, a custom SQL view using an operator can require its `PgOperator.Id`.

`PgFunction.Sql` replaces the backing function and its attached operator/cast SQL
together; `GenerateSql = false` suppresses all those statements while retaining
the native entry point and dependency IDs. External prerequisites precede the
complete replacement. See [function SQL controls](/custom-sql/#replace-function-sql)
for placeholders, relocation and ordering constraints.

The generator reports [specific declaration diagnostics](#declaration-diagnostics)
for invalid or duplicate operators and casts, and
[dependency graph diagnostics](/custom-sql/#dependency-graph-diagnostics) for
invalid `Id` and `Requires` values, `[PgBefore]` references and cycles. PostgreSQL
validates database-dependent contracts during installation. An existing cast for
the same source/target pair or an existing fully defined operator is an installation
error, even when the backing function uses `CreateOrReplace`.

PostgreSQL owns the generated objects as extension members. `DROP EXTENSION`
removes them, and a relocatable extension can move its enum, functions and
operators while retaining its casts.

The [operators sample](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.Operators)
ports pgrx's `operators` example. It declares a manual `=` operator, generated
equality, B-tree and hash operators for a JSON-text type and a packed native
type, and `===`/`!==` operators and a cast for its `Priority` enum.

## Declaration diagnostics

These errors identify the affected attribute value, result type, operand type,
`params` modifier or argument list. Injected contexts do not count as SQL
arguments. The same diagnostics apply to ordinary methods, C# operators and
conversion declarations.

| Diagnostic | Required correction |
| --- | --- |
| ANKUS064 | Return one value instead of a set. |
| ANKUS065 | Use 1–63 characters from PostgreSQL's operator set (`+-*/<>=~!@#%^&\|?` and the backtick), without `--`, `/*`, an ambiguous trailing `+` or `-`, or the reserved `=>`. |
| ANKUS066 | Declare one prefix operand or two binary operands. |
| ANKUS067 | Replace the variadic parameter with fixed SQL arguments. |
| ANKUS068 | Return a supported SQL value instead of `void`. |
| ANKUS069 | Choose a distinct negator with the complementary result. |
| ANKUS070 | Use the named planner option only on a binary operator. |
| ANKUS071 | Use the named planner option only on an operator returning SQL `boolean`. |
| ANKUS072 | Name one commutator or negator, optionally qualified by one schema. |
| ANKUS073 | Name one estimator function, optionally qualified by one schema. |
| ANKUS074 | Select `Explicit`, `Assignment` or `Implicit` as the cast context. |
| ANKUS075 | Declare one to three SQL arguments for the cast. |
| ANKUS076 | Use non-nullable `int` for the cast's optional type modifier. |
| ANKUS077 | Use non-nullable `bool` for the cast's optional explicit-conversion flag. |
| ANKUS078 | Give composite endpoints concrete SQL identities with `PgCompositeType`. |
| ANKUS079 | Convert between distinct SQL types in a one-argument cast. |
| ANKUS508 | Change the reported operator's name, schema or operand types; another operator already has that signature. CLR aliases, nullability and `!=` versus `<>` do not create distinct PostgreSQL operators. |
| ANKUS509 | Keep one cast for each source and target type. Cast context, type-modifier parameters and the backing function's schema do not create distinct PostgreSQL casts. |

`ANKUS508` and `ANKUS509` point at the later `PgOperator` or `PgCast` attribute
and name the duplicated catalog signature. Editors offer **Remove** for the
option reported by `ANKUS069`, `ANKUS070` or `ANKUS071`, **Remove `params`** for
`ANKUS067`, and **Change the type** to `int` or `bool` for a cast's type modifier
or explicit-conversion parameter (`ANKUS076`, `ANKUS077`).
