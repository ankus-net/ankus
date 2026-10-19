# PostgreSQL type variants example

This ports pgrx's `postgres_type_variants` example: side-by-side ways to define
a custom PostgreSQL type's input, output and storage, each in the smallest
runnable form.

```sql
CREATE EXTENSION ankus_type_variants;

SELECT coord_translate('{"x":1.0,"y":2.0}'::coord, 10.0, 20.0);   -- {"x":11,"y":22}
SELECT complex_add('1+2i'::complex, '3+4i'::complex);             -- 4+6i
SELECT '#FF8000'::rgb, rgb_luminance('#ffffff'::rgb);             -- #ff8000, 255
SELECT '16777215'::u24, '16777216'::u24;                          -- 16777215, then ERROR: value exceeds 24 bits
SELECT sum_pair_a(ARRAY['{"a":10,"b":"x"}'::pair, NULL::pair]);   -- 10
SELECT make_pairs();                                               -- {"{\"a\":1,\"b\":\"one\"}","{\"a\":2,\"b\":\"two\"}"}
```

## Which variant should I use?

| File | pgrx | Ankus | Text | Storage | Pick when |
|---|---|---|---|---|---|
| `JsonDefault.cs` | `#[derive(PostgresType, Serialize, Deserialize)]` | `[PgType]` | Generated JSON | Generated CBOR | The value is JSON-shaped or you are prototyping and do not need a custom text form. |
| `CustomText.cs` | `#[inoutfuncs]` and `impl InOutFuncs` | `[PgType(TextCodec = ...)]` with a `PgTypeTextCodec<T>` | Your `Parse` and `Format` | Generated CBOR | SQL should use a domain syntax, such as `3+4i`. |
| `PackedNative.cs` | `#[pgvarlena_inoutfuncs]`, `impl PgVarlenaInOutFuncs` and `PgVarlena<T>` | `[PgType(NativeLayout = true, TextCodec = ...)]` and `PgVarlena<T>` | Your `Parse` and `Format` | The struct's packed bytes | Many rows are read and a stable, tight binary layout pays off. |
| `HandRolledDatum.cs` | Manual `FromDatum`/`IntoDatum`, `SqlTranslatable` and `extension_sql!` | `[PgDatumType]` converter, `[PgSql]` blocks, `[PgSqlTypeProvider]` and `[PgFunction]` input/output functions | Your input and output functions | Whatever your converter reads and writes | You need full control of the SQL representation, length, alignment or pass-by-value storage. |
| `CompositeAndArray.cs` | `PostgresType` on a record-shaped struct with `Vec<T>` and `Array<T>` | `[PgType]` record with `Pair[]` and `Pair?[]` | Generated JSON | Generated CBOR | You need arrays of the type, including SQL NULL elements. |

The variants differ in who owns each part of the contract. `[PgType]` generates
the shell type, input/output functions and array type. `TextCodec` replaces
only the text conversion; `NativeLayout` also replaces the CBOR storage with the
struct's exact bytes, which `PgVarlena<T>` reads in place. `[PgDatumType]`
generates neither SQL nor storage: the converter, the SQL blocks and the
input/output functions together define the type, as pgrx's hand-written traits
and `extension_sql!` blocks do.

## pgrx mapping

| pgrx | Ankus |
| --- | --- |
| `struct Coord { x: f64, y: f64 }` with Serde | `record Coord(double X, double Y)` with `[JsonPropertyName("x")]` and `[JsonPropertyName("y")]` |
| `impl InOutFuncs for Complex` | `ComplexTextCodec : PgTypeTextCodec<Complex>` |
| `#[repr(C)]`-compatible `Rgb { r, g, b }` | `[StructLayout(LayoutKind.Sequential, Pack = 1)] record struct Rgb(byte R, byte G, byte B)` |
| `fn rgb_luminance(c: PgVarlena<Rgb>)` | `RgbLuminance(PgVarlena<Rgb> c)` |
| `struct U24(u32)` with `FromDatum`, `IntoDatum`, `ArgAbi`, `BoxRet` | `[PgDatumType("u24", typeof(U24Converter))] record struct U24(uint Value)` |
| `extension_sql!("CREATE TYPE u24;", bootstrap)` | `[assembly: PgSql("u24_shell", ..., Order = PgSqlOrder.Bootstrap)]` |
| `extension_sql!(..., creates = [Type(U24)], requires = [...])` | `[assembly: PgSql("u24_concrete", ..., Requires = [...])]` and `[assembly: PgSqlTypeProvider("u24_concrete", typeof(U24))]` |
| `fn u24_in(input: &CStr) -> Result<U24, Box<dyn Error>>` | `[PgFunction(Name = "u24_in")] U24 Input(PgCStringView input)` |
| `fn u24_out(value: U24) -> &'static CStr` | `[PgFunction(Name = "u24_out")] PgCString Output(U24 value)` |
| `fn make_pairs() -> Vec<Pair>` | `Pair[] MakePairs()` |
| `fn sum_pair_a(arr: Array<Pair>)` with `.flatten()` | `long SumPairA(Pair?[] arr)`, skipping null elements |

## Deliberate differences

- pgrx's text input errors are panics with SQLSTATE `XX000`, and Rust's
  `Result::expect` appends the parse error's debug text. The Ankus codecs raise
  `22P02` (`invalid_text_representation`) with pgrx's message text only.
  `u24_in` keeps pgrx's `22000` and Rust's integer parse messages.
- Floating-point text uses .NET's round-trip formatting. JSON writes `3` where
  Serde writes `3.0`, and very large or small `complex` parts use exponents such
  as `1E+21` where Rust writes every digit. Parsing uses .NET invariant number
  syntax, which spells infinity `Infinity` where Rust uses `inf`.
- `rgb` accepts exactly two hexadecimal digits per component. Rust's
  `from_str_radix` would also accept a leading `+`.
- The type names follow Ankus's snake-case default: `coord`, `complex`, `rgb`,
  `pair`. Unquoted pgrx names fold to the same identifiers.
- pgrx's README also lists `enum_and_ord.rs`, which the example does not contain.
  [`Ankus.Examples.Enums`](../Ankus.Examples.Enums/) and the `OrderedKey` type in
  [`Ankus.Examples.CustomTypes`](../Ankus.Examples.CustomTypes/) cover enums and
  generated comparison operators.
- The integration tests mirror pgrx's `#[pg_test]` cases and add text
  round-trips, error SQLSTATEs and messages, the packed payload size, the `u24`
  catalog representation and arrays with SQL NULL elements.

See [custom types](https://ankus.net/custom-types/),
[reusable scalar mappings](https://ankus.net/raw-values/#reusable-scalar-mappings)
and [custom SQL](https://ankus.net/custom-sql/#shell-types-and-completion).
