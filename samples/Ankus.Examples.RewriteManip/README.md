# Rewrite manipulation example

This ports pgrx's `rewrite_manip` example. It calls PostgreSQL's
`rewrite/rewriteManip.h` query-tree utilities through Ankus's generated native
bindings. Each function creates one `Var` node, the internal representation of
a column reference, passes it to a rewriter utility and returns the changed
field.

| Function | Description |
| --- | --- |
| `demo_change_var_nodes(old_varno, new_varno)` | Remaps a `Var`'s range-table reference with `ChangeVarNodes` |
| `demo_offset_var_nodes(offset)` | Shifts a `Var` for range-table entry 1 with `OffsetVarNodes` |
| `demo_range_table_entry_used(varno, check_rt_index)` | Checks whether `rangeTableEntry_used` finds a reference |
| `demo_increment_var_sublevels_up(initial_level, delta)` | Changes a `Var`'s subquery level with `IncrementVarSublevelsUp` |

The planner and rewriter use these utilities to combine range tables when they
flatten subqueries, and to adjust column references after changing a `FROM`
list.

```sql
CREATE EXTENSION ankus_rewrite_manip;

SELECT rewrite_manip.demo_change_var_nodes(1, 5);            -- 5
SELECT rewrite_manip.demo_offset_var_nodes(3);               -- 4 (1 + 3)
SELECT rewrite_manip.demo_range_table_entry_used(3, 3);      -- true
SELECT rewrite_manip.demo_range_table_entry_used(3, 5);      -- false
SELECT rewrite_manip.demo_increment_var_sublevels_up(0, 2);  -- 2
```

The control file installs the functions in the fixed `rewrite_manip` schema,
which `CREATE EXTENSION` creates. The extension cannot be relocated.

`initial_level` and the result of `demo_increment_var_sublevels_up` reinterpret
PostgreSQL's unsigned `Index` as a signed integer, as pgrx's `as` casts do:
`demo_increment_var_sublevels_up(0, -1)` returns `-1`.

On PostgreSQL 16 and later, a `Var` also records the outer joins that can null
it. `rangeTableEntry_used` looks up the searched index in that set, so a
negative index raises PostgreSQL's `negative bitmapset member not allowed`
error with SQLSTATE `XX000`. The error crosses Ankus's native guard as a
`PgException`; the statement fails and the session continues. Earlier versions
return `false`.

## pgrx mapping

| pgrx | Ankus |
| --- | --- |
| `pg_sys::makeVar(...)` | `NativeMethods.makeVar(...)` returning `Var*` |
| `pg_sys::ChangeVarNodes(var as *mut pg_sys::Node, ...)` | `NativeMethods.ChangeVarNodes((Node*)var, ...)` |
| `pg_sys::OffsetVarNodes`, `rangeTableEntry_used`, `IncrementVarSublevelsUp` | `NativeMethods` with the same names |
| `pg_sys::INT4OID`, `pg_sys::InvalidOid` | `(uint)PgBuiltInOid.Int4Oid`, `0` |
| `unsafe { ... }` with `(*var).varno` | An `unsafe` block with `var->varno` |
| `old_varno.try_into().unwrap()` | A checked conversion on PostgreSQL 13 and 14, whose `Index` is unsigned |
| `#[pg_extern] fn demo_change_var_nodes(old_varno: i32, ...)` | `[PgFunction] DemoChangeVarNodes(int oldVarno, ...)` |
| `schema = 'rewrite_manip'` in the control file | The same setting in `ankus_rewrite_manip.control` |

## Deliberate differences

- PostgreSQL 13 and 14 declare `Var.varno` and `makeVar`'s `varno` as unsigned
  `Index`; PostgreSQL 15 and later use `int`. pgrx converts with
  `try_into().unwrap()`, which panics for a negative index on the older
  versions. Ankus selects the declaration with `#if ANKUS_PG13 || ANKUS_PG14`
  and throws `ArgumentOutOfRangeException` there, which PostgreSQL reports with
  SQLSTATE `38000`.
- pgrx's tests run through SPI inside a test backend. The Ankus integration
  tests call the published functions from a client in a rolled-back transaction,
  and add boundary indexes, NULL arguments, the installed signatures and the
  PostgreSQL 16 error with recovery in the same backend.

See [native PostgreSQL declarations](https://willibrandon.github.io/ankus/raw-values/#native-postgresql-declarations)
for raw node pointers, `NativeMethods` and the native error guard.
