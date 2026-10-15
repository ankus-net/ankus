# Minimal runtime example

pgrx's `nostd` example checks that pgrx's derives compile under `#![no_std]`,
without Rust's standard library. .NET has no counterpart: a Native AOT library
always contains the .NET runtime, including its garbage collector, type system
and exception handling, and Ankus's managed boundary relies on them.

This sample is the honest equivalent. It compiles the same declarations with the
optional runtime features that Native AOT can remove turned off:

```xml
<InvariantGlobalization>true</InvariantGlobalization>
<UseSystemResourceKeys>true</UseSystemResourceKeys>
<StackTraceSupport>false</StackTraceSupport>
<OptimizationPreference>Size</OptimizationPreference>
```

- `InvariantGlobalization` removes culture data and does not load ICU into the
  backend. Culture-sensitive .NET operations behave like the invariant culture;
  PostgreSQL's own collations are unaffected.
- `UseSystemResourceKeys` replaces .NET's exception messages with resource keys.
  Messages you write, such as `PgException` text, are unchanged.
- `StackTraceSupport` removes method names from exception stack traces.
- `OptimizationPreference` favors smaller code.

See Microsoft's [Native AOT deployment](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)
and [trimming options](https://learn.microsoft.com/dotnet/core/deploying/trimming/trimming-options)
for these switches.

```sql
CREATE EXTENSION ankus_minimal_runtime;

SELECT hello_nostd();                                     -- Hello, nostd
SELECT echo('café 🐘');                                   -- café 🐘
SELECT '{"value":1}'::my_type = '{"value":1}'::my_type;   -- true
CREATE TABLE things(value thing);
CREATE INDEX ON things USING btree (value);
CREATE INDEX ON things USING hash (value);
```

## pgrx mapping

| pgrx | Ankus |
| --- | --- |
| `#![no_std]`, `extern crate alloc` | Native AOT feature switches; there is no runtime-free mode |
| `#[derive(PostgresType, Serialize, Deserialize)] struct Thing(String)` | `[PgType] record Thing(string Value)` |
| `#[derive(PostgresEq, PostgresOrd, PostgresHash)]` with `Eq`, `Ord`, `Hash` | `[PgEquality]`, `[PgOrdering]`, `[PgHashing]` with `IComparable<Thing>` and `IPgHashable` |
| `struct MyType { value: i32 }` and `#[pg_operator] #[opname(=)] fn my_eq` | `record MyType(int Value)` and `[PgOperator("=")] MyEq` |
| `fn hello_nostd()`, `fn echo(input: String)` | `HelloNostd()`, `Echo(string input)` |

## Deliberate differences

- `Thing` stores `{"Value":"text"}`; Serde writes a newtype struct as the bare
  string. `Thing` orders by UTF-8 bytes, matching Rust's `String` ordering, so
  U+FFFF sorts before U+10000, unlike .NET's ordinal UTF-16 comparison.
- `MyType` is named `my_type`, following Ankus's snake-case default.
- pgrx's example has no tests. The Ankus integration test runs the functions and
  operators and builds B-tree and hash indexes under this configuration.

See [Running .NET inside PostgreSQL](https://willibrandon.github.io/ankus/reference/execution/#managed-memory-and-database-memory)
and [Ankus for pgrx users](https://willibrandon.github.io/ankus/getting-started/from-pgrx/).
