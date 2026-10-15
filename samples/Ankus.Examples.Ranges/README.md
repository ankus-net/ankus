# Ranges

This ports the constructors and stored-range example from
[pgrx's range sample](https://github.com/pgcentralfoundation/pgrx/tree/70383e884582d1bcc7cd681d10886b995a2830cb/pgrx-examples/range).
Publish and install this project with the Ankus tool, then load it in a schema:

```sql
CREATE SCHEMA ranges;
CREATE EXTENSION ankus_ranges WITH SCHEMA ranges;
```

| SQL expression | Result |
|---|---|
| `ranges.range(10, 101)` | `[10,101)` |
| `ranges.range_from(10)` | `[10,)` |
| `ranges.range_full()` | `(,)` |
| `ranges.range_inclusive(10, 100)` | `[10,101)` |
| `ranges.range_to(101)` | `(,101)` |
| `ranges.range_to_inclusive(100)` | `(,101)` |
| `ranges.empty()` | `empty` |
| `ranges.infinite()` | `(,)` |
| `ranges.assert_range('[10,101)', 10, 101)` | `true` |

The C# methods construct `PgRange<int>` without calling PostgreSQL. Native
conversion validates and canonicalizes their bounds. Equal exclusive ends become
empty; reversed bounds and an inclusive upper `int.MaxValue` raise PostgreSQL
errors. A NULL required input produces SQL NULL through the generated strict
function declaration.

Store ranges like ordinary PostgreSQL values:

```sql
CREATE TABLE stored_ranges(id integer PRIMARY KEY, value int4range);
INSERT INTO stored_ranges
SELECT step, ranges.range(100, 100 + step)
FROM generate_series(0, 100) AS steps(step);

SELECT id, value FROM stored_ranges ORDER BY id;
-- First row: 0, empty. Last row: 100, [100,200).
```

`AssertRange` compares detached representations. An empty range differs from a
newly requested pair of equal bounds until that pair has been canonicalized.
For PostgreSQL semantic equality, use SQL `=` or call `Canonicalize()` first.
See the [range guide](https://ankus.net/ranges/) for other bound
families, operations, arrays and explicitly mapped custom bounds.
