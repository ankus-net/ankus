# SPI queries and table functions

This sample ports pgrx's `spi` and `spi_srf` examples. Publish and install this
project with the Ankus tool, then load it:

```sql
CREATE EXTENSION ankus_spi;

SELECT spi.spi_query_by_id(1);                 -- This is a test
SELECT spi.spi_query_title('Hello There!');    -- 2
SELECT spi.spi_insert_title('A new title');
SELECT * FROM spi.spi_insert_title2('Another title');
SELECT * FROM spi.spi_return_query();
SELECT spi.issue1209_fixed();                  -- hello

SELECT * FROM spi_srf.calculate_human_years() ORDER BY dog_age;
SELECT * FROM spi_srf.filter_by_breed('Labrador') ORDER BY dog_age;
```

The extension owns the fixed `spi` and `spi_srf` schemas, their example tables,
and their seed data. It cannot be relocated to another schema. Removing the
extension removes those objects; reinstalling restores the original examples.

`Spi.Sql` sends interpolated values as PostgreSQL parameters. Quotes, newlines,
and SQL-looking titles remain ordinary data. Missing scalar rows and nullable
columns return C# null. Required inputs generate strict SQL functions, so a
NULL argument does not execute the method.

`Spi.Connect` scopes a session. These examples materialize owned managed values
inside that scope before returning them. The cursor example fetches ten thousand
strings, disposes its cursor, leaves the session, and then returns the first
string. The table functions return named C# tuples as SQL rows.

The age calculation preserves nullable names and breeds, requires a non-NULL
age, and uses checked multiplication. Invalid ages raise an error; the caller
can recover with an ordinary PostgreSQL savepoint. The breed filter preserves
NULL ages and never converts them to zero.

Functions use volatile, parallel-unsafe declarations because they access backend
state. In particular, the random lookup must remain volatile even though pgrx's
original example marks it immutable. See the [SPI guide](https://ankus.net/spi/)
for snapshots, transaction behavior, ownership, and error recovery.
