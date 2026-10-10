# Native declaration catalogs

The generated `pg13.json` through `pg19.json` files retain native node tags,
structs/unions, fields, typedefs, enums and node cast sets from pgrx commit
`fc91c63ebad11784647b50ee7e265c1fd9c9924f` (pgrx 0.19.3). They preserve complete bindgen type
expressions and do not assert physical sizes or offsets. Physical layouts must
come from the selected PostgreSQL headers and target toolchain.

The matching `pg13.raw.json` through `pg19.raw.json` files inventory foreign
functions, globals and top-level reference constants from that same commit.
Functions retain parameter order, complete callback and array expressions,
return types, variadic markers, ABI and declaration attributes. Globals retain
their declared type and mutability. `NativeSymbol` preserves `link_name`, including
pgrx's `__pgrx_cshim` symbols; those names are not PostgreSQL exports. Hook aliases
resolve through the corresponding type catalog. Both inventories preserve each
major's own signatures instead of applying the newest signature to older servers.

Selected-header helpers supplement declarations missing from a major's foreign
inventory. This includes `pg_add_s32_overflow` from `common/int.h` on all seven
majors. The compiler measures its actual signature and the native call uses the
installed header implementation; the pinned inventories remain unchanged.

`ReferenceConstants` retain unevaluated expressions from pgrx's reference build.
They include configuration values and platform-specific widths. Never use them
as measurements of the extension's selected target; use its headers and compiler.
The raw inventory records its source revision and is loaded independently of the
node layout graph.

| PostgreSQL input | Foreign functions | Foreign globals | Reference constants |
|---|---:|---:|---:|
| 13 | 8,666 | 552 | 5,664 |
| 14 | 9,058 | 580 | 6,353 |
| 15 | 9,294 | 595 | 6,473 |
| 16 | 9,622 | 615 | 6,564 |
| 17 | 9,814 | 636 | 6,691 |
| 18 | 10,204 | 686 | 6,861 |
| 19 | 10,424 | 750 | 6,939 |

The reference server versions are 13.23, 14.24, 15.19, 16.15, 17.11,
18.6 and 19 beta 4. The OID catalog uses the same pgrx revision; SQLSTATE
generation reads PostgreSQL's `REL_19_BETA4` tag. Selected-header measurements
remain authoritative for each consumer's installation.

Regenerate catalogs and attribution with `eng/Ankus.Bindings.cs`. Regenerate each
`pgXX.h` directly from that major's installed PostgreSQL server headers with
`eng/Ankus.Headers.cs`. The manifest generation follows pgrx 0.19.3's source
rules and does not copy pgrx's already-generated output. The binding app's check
mode compares those source-derived files with the pinned pgrx release bytes as a
separate drift guard. Do not edit generated inputs by hand. Builds derive their
own declarations from the selected headers, described below; these pinned files
are the reference those derivations are compared against. They are not
evidence that all raw PostgreSQL functions, globals, callbacks or node APIs are
implemented.

## Header-derived catalogs

The build tool can also derive each major's type catalog and its foreign
function and global inventory from the installed headers instead of pgrx's
generated bindings. Clang 20 or later serializes the manifest's AST, and a
worker reads it through the compiler's own libclang, as bindgen does. It follows
pgrx 0.19.3's bindgen configuration: PostgreSQL's include directories and the
`PGERROR` and `SIG*` items are allowlisted with every type they reference, the
blocklists apply, enums become constant modules except the rustified
`NodeTag`, nested anonymous types take their parent's name, and union members
that cannot be copied are wrapped in `ManuallyDrop`. It also reproduces the forms
that depend on how bindgen parses a declaration. A tag declared at file scope
but never defined is an empty forward declaration, and one named only inside
another record has an `_address` byte. Bitfield units and `__bindgen_padding_N`
fields follow bindgen's layout tracker over Clang's record layouts. Callback
types keep their parameter names, and a parameter written as `va_list` keeps
its aliases. Functions follow bindgen's codegen: the first declaration of a
name wins, hidden functions and variadic static functions are skipped, a static
function links to pgrx's C shim wrapper as `name__pgrx_cshim`, `noreturn`
functions return `!`, and documentation comments become `#[doc]` attributes.
A variable whose initializer Clang evaluates is a constant rather than a
global.

Reference constants come from the same translation unit. Every object-like
macro is evaluated in definition order with a port of the cexpr expression
evaluator bindgen uses, so later macros see earlier ones, while pgrx's ignored
macros and function-like macros define nothing. The allowlisted ones are typed
as bindgen types them: integers as `u32`, or `i32` when negative, widening to
64 bits when needed; characters as `u8`; floating-point numbers as `f64`; and
strings as C string literals. A redefined macro keeps its first value, and
pgrx's built-in OID constants become `Oid`.

For PostgreSQL 13.23, 14.24, 15.19, 16.15, 17.11, 18.6 and 19 beta 4 on Linux
x64, the derived catalogs match the pinned ones, apart from formatting, except
where the installation was configured differently from pgrx's reference
build: assert-only declarations, configure options such as `USE_ICU`, NLS and
liburing declarations, version strings, the default port and socket directory,
Debian's `extension_destdir` patch, and the C library's own `_IO_FILE` layout.
Against a PostgreSQL 19 beta 4 built by `ankus init` like pgrx's reference,
only the C library layout, the port and the version string differ. pgrx
re-exports `yytoken_kind_t` for 13 through 15 with `pub use`, and the pinned
catalogs record it as an alias so both sources agree.

Extension builds use the derived catalogs. `binding-sources` derives them with
the build's own Clang options and libclang and caches them by the preprocessed
manifest, toolchain and build tool, so a reused binding still reflects the
selected headers. The pinned catalogs remain the reference for this comparison
and for the build tool's own tests. Compare an installation's derived catalogs with
the pinned ones using `eng/Ankus.Bindings.cs -- --headers`.
