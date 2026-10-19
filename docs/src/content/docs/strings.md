---
title: Strings sample
description: Full Unicode casing, exact UTF-8 byte slices and terminator-aware string arrays and sets.
---
This ports pgrx's strings example to ordinary C# functions. It preserves exact
text, UTF-8 byte offsets and the separator rules used by Rust's
`split_terminator`.

```sql
SELECT return_static();                     -- This is a static string xxx
SELECT append('hi', ' there');              -- hi therex
SELECT to_lowercase('İ ΟΣ');                -- i + combining dot, then ος
SELECT public.substring('Aé中😀Z', 6, 10);         -- 😀
SELECT split(',a,,b,', ',');                -- {"",a,"",b}
SELECT * FROM split_set(',a,,b,', ',');
SELECT i, s FROM split_table(',a,,b,', ',');
```

The example assumes installation in `public`. Use your extension's schema when
qualifying `substring`: PostgreSQL's built-in function has the same name and
signature but uses character positions and a length. The sample's function
uses byte bounds.
The table form preserves the upstream `i` and `s` column names and zero-based
ordinals. Sets stream pieces in source order; use the ordinal column with
`ORDER BY i` when a query needs explicit ordering.

Lowercase uses full Unicode **17.0** mappings, including dotted-I expansion,
contextual final sigma and supplementary scripts. It is independent of thread
culture, database collation and the platform's globalization provider. The data
matches the reference Rust **1.97.1** standard library. It is not PostgreSQL's
collation-dependent `lower` function.

`substring` takes start-inclusive and end-exclusive **UTF-8 byte offsets**.
Both offsets must be scalar boundaries, including empty slices. Negative,
reversed, out-of-range or mid-scalar offsets are errors. The borrowed native
bytes are consumed before returning an independent managed string.
These ordinary C# failures report SQLSTATE **38000**; pgrx's Rust slice panics
report **XX000**. Both fail the statement and require transaction recovery.

Splitting preserves leading and interior empty pieces and removes only the
final terminator suffix. An empty separator produces a leading empty piece and
then one piece per Unicode scalar. Empty input with an empty separator therefore
produces one empty piece; empty input with a nonempty separator produces none.
Matching is ordinal, without case folding or Unicode normalization.

All arguments are non-nullable, so Ankus declares the functions `STRICT`: a NULL
argument returns NULL without calling them.
SQL NULL is distinct from present empty text, an empty array and an empty string
row. Set/table iterators retain managed text across native callbacks.

The implementation is MIT-licensed. The sample's third-party notice preserves
the upstream pgrx license and the [Unicode data notice](https://www.unicode.org/license.txt).
