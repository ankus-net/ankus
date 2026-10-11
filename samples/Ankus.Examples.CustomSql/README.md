# Custom SQL example

This sample ports pgrx's `custom_sql` example. It adds hand-written SQL to the
installation script, from C# strings and from `.sql` files, and orders it
around generated schemas and types. Each block records its name in a table, so
the table shows the order in which `CREATE EXTENSION` ran them:

```sql
CREATE EXTENSION ankus_custom_sql;

SELECT message FROM extension_sql;
-- bootstrap
-- single_raw
-- single
-- multiple_raw
-- multiple
-- finalizer

SELECT enum_range(NULL::dogs.dog);                 -- {Brandy,Nami}
SELECT '{"last_chomp":"Nami"}'::home.ball;
```

## How pgrx maps to C#

| pgrx | Ankus |
| --- | --- |
| `extension_sql!("...", name = "single_raw")` | `[assembly: PgSql("single_raw", "...")]` |
| `extension_sql_file!("../sql/single.sql")` | `[assembly: PgSqlFile("sql/single.sql")]`, with the file in `AdditionalFiles`; both name the block `single` |
| `bootstrap` | `Order = PgSqlOrder.Bootstrap` |
| `finalize` | `Order = PgSqlOrder.Finalize` |
| `requires = ["single_raw"]` | `Requires = ["single_raw"]` |
| `requires = [Dog, home::Ball]` | `[assembly: PgRequires(typeof(Home.Dogs.Dog), DeclarationId = "multiple_raw")]` |
| `requires = [home::dogs]` | `[assembly: PgRequires(typeof(Home.Dogs), DeclarationId = "single_raw")]` |
| `#[pg_schema] mod home { #[pg_schema] mod dogs { ... } }` | `[PgSchema("home")]` class containing a `[PgSchema("dogs")]` class |
| `#[derive(PostgresEnum)] enum Dog` | `[PgEnum] enum Dog` |
| `#[derive(PostgresType)] struct Ball` | `[PgType] record Ball` |

String dependency IDs name SQL blocks, including SQL files. `PgRequires`
names a generated declaration by its C# type: a schema class, an enum or a
custom type. The bootstrap block runs before every generated schema, type and
function, and the final block runs after everything else. An extension has at
most one of each. Ankus orders independent declarations deterministically; the
order of attributes in the C# source is not a dependency.

PostgreSQL schemas do not nest, so `Home.Dogs` creates a top-level `dogs`
schema, as pgrx's nested module does. The C# `Ball` record keeps pgrx's
`last_chomp` field name with `[JsonPropertyName]`. The enum is stored by name.

The generated script keeps each block's text and adds comments with its source
location, ID and dependencies. Build-time generation reads SQL files; the
published extension does not read them at run time.

## Deliberate differences

- **SQL file names.** pgrx defaults a file block's name to the file stem.
  `PgSqlFile` always takes the name as its first argument; the sample uses the
  same stems: `single`, `multiple` and `finalizer`.
- **Positioning by type.** pgrx accepts Rust paths inside `requires`. C#
  attributes cannot mix strings and types in one array, so type references use
  separate assembly-level `PgRequires` attributes with `DeclarationId` naming the
  block being ordered.
- **SQL file headers.** pgrx's SQL files repeat its license header. The port's
  files contain only the statement.
