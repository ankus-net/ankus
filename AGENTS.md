# Working on Ankus

Ankus is a faithful port of pgrx to idiomatic .NET Native AOT. The full scope and
verified evidence live in `PROGRESS.md`; do not treat a passing subset as full
parity. Read `README.md`, the relevant progress entries, and related public docs
before changing an area.

## Reference implementations

- `pgrx` is the read-only source for pgrx APIs, examples, and tests.
- `postgres` is the read-only source for PostgreSQL ABI and backend behavior.
- [willibrandon/pglogical](https://github.com/willibrandon/pglogical) is a read-only
  reference for Windows background-worker startup and shared-memory attachment.
  Preload and worker behavior must work on Linux, macOS, and Windows.
- [ankus-net/runtime](https://github.com/ankus-net/runtime) hosts the runtime
  fork. Keep the reference clone read-only; develop the patch in a separate checkout.
- [apple-oss-distributions/libpthread](https://github.com/apple-oss-distributions/libpthread)
  is a read-only reference for macOS thread and fork behavior.
- [apple-oss-distributions/xnu](https://github.com/apple-oss-distributions/xnu)
  is a read-only reference for Mach thread enumeration and port lifetimes.
- Other available reference repositories include `roslyn`, `sdk`,
  `msbuild`, `NuGet.Client`, `docs`, `dotnet-api-docs`, and `dotsider`.
  Locate available local checkouts before requesting another clone; treat all
  reference repositories as read-only.
- Use the supported PostgreSQL version headers when emitting C; do not assume
  that internal layouts or APIs are identical across versions.

## Implementation conventions

- Prefer ordinary C# APIs, attributes, source generation, NuGet project SDKs,
  `dotnet publish`, and `dotnet test` while preserving PostgreSQL semantics.
- Keep Native AOT consumers free of runtime code generation and unbounded reflection.
- Never use locally patched serializers. Write our own serializer.
- Only use NuGet packages owned by Microsoft and/or .NET.
- PostgreSQL ERROR/longjmp must never cross a managed frame. Guard backend calls
  in native code, transport owned diagnostics, and raise errors only after managed
  frames have unwound. Match allocators and test cleanup and backend recovery.
- Preserve exact datum values, SQL NULL, type identity, shape, encoding, and
  lifetime contracts. Reject lossy conversions explicitly.
- Follow the `runtime` and `msbuild` type-style convention: use explicit local
  types when the right-hand side does not name the type. `var` is permitted, not
  required, for clear constructors and casts. `.editorconfig` enforces the
  built-in and non-apparent cases through IDE0008; do not enable IDE0007 globally.
- Use primary constructors wherever IDE0290 applies. The repository enforces
  this rule as an error; preserve constructor validation,
  accessibility, and native struct layouts when converting existing declarations.
- Remove redundant casts. IDE0004 is enforced as an error throughout the repository.
- Remove unnecessary `unsafe` modifiers. IDE0380 is enforced as an error.
- Mark struct members `readonly` wherever IDE0251 applies. IDE0251 is enforced
  as an error throughout the repository; fix all findings without suppression.
- Use collection expressions where IDE0300 applies. IDE0300 is enforced as an error
  throughout the repository.
- Never disable warnings. Fix the underlying issue without warning pragmas,
  suppression attributes, `NoWarn`, or reducing an enforced diagnostic's severity.
- Keep `MSTestAnalysisMode` set to `All` for every repository project, with
  warnings treated as errors. Never relax analyzer standards or modes; fix every
  diagnostic without suppression.
- Use `InternalsVisibleTo` only for test assemblies. Give production tools a
  deliberate command or API boundary instead of access to another assembly's
  internals.
- Do not leave an extra blank line immediately after an opening brace.
- Put block braces on their own lines, including short `if`, `else`, and loop
  bodies. Do not compress statements and their braces onto one line.
- Keep simple getter-only declarations on one line, including auto-properties,
  partial properties, interface properties, and any initializer.
  Properties with `get` and `set` or `init` use separate accessor lines inside
  the property braces, including accessors with visibility modifiers.
- Leave a blank line after a closing block brace before the next statement or
  declaration. Keep connected `else`, `catch`, and `finally` clauses together;
  adjacent enclosing closing braces do not need a blank line. IDE2003 enforces
  statement separation in repository builds.
- Keep repository coding style out of consumer templates. `ankus new` must not
  impose this repository's `.editorconfig` rules or code-style build enforcement.
- Document public and internal declarations with XML comments. Put XML summary
  opening tags, text, and closing tags on separate lines. Follow the analyzers;
  generic types must not expose static members (CA1000).
- Write repository automation as .NET file-based C# apps. Do not add Bash,
  PowerShell, or `.env` scripts. Each folder containing file-based apps must have
  a README that briefly lists each app and its purpose.

## Verification

- Follow pgrx's approach: test values and helpers directly, test generated
  contracts and diagnostics, and execute the published Native AOT extension in
  PostgreSQL to prove backend behavior.
- Tests use the pinned MSTest SDK and native Microsoft.Testing.Platform mode in
  `global.json`. Run the narrow affected scope during development, then plain
  `dotnet test` for the completed change. See `docs/contributing/development.md`
  for prerequisites and fixture behavior.
- Verify observable boundaries, errors, ownership, and same-session recovery;
  test counts and generated-source substrings alone do not prove parity.
- Keep CI feedback under 10 minutes where possible. Set each workflow job's
  timeout from measured duration with enough headroom to avoid wasting a nearly
  complete full-suite run. Record measured durations and timeout outcomes. Run
  independent platform checks in parallel and measure cold-cache builds. Let
  queued and in-progress runs finish. Do not hide missing validation to meet the
  budget.
- Run the complete test suite in each platform job; do not shard it. CI runs on
  Linux x64, macOS ARM64, and Windows x64. Releases also include macOS x64.
- CI platform evidence must run the full test suite against a real PostgreSQL
  server. A generated-source check or focused smoke test is not platform proof.
- Run a Release build and relevant documentation checks before committing.
  Record exactly which PostgreSQL versions and platforms were actually tested.
- For personal validation machines, document platform versions and test evidence
  only. Keep machine names, addresses, usernames, connection details and personal
  device paths out of repository documents, including `PROGRESS.md`.

## Progress, documentation, and commits

- Update `PROGRESS.md` with implementation, test evidence, and remaining scope
  as work progresses. Keep unresolved full-port requirements visible.
- Update related public guides in `docs/src/content/docs/` and the README when
  appropriate. Generate API pages from source XML comments using
  `dotnet run --project src/Ankus.DocGenerator -c Release`; do not hand-edit them.
- Write docs, comments and messages as direct statements of what something is
  and does. Do not pair contrasts such as "X, not Y", "not X but Y" or
  "X rather than Y"; state a real caveat as its own plain clause.
- Write commit and merge messages as one short subject line, usually with no
  body. Give every PR a short plain description of one or two sentences. Never
  write explanatory prose, lists, headers or em dashes in either.
- Keep repository maintenance commands in `eng/README.md` or `docs/contributing/`.
  Public user guides should describe extension-author workflows, supported APIs,
  and their limitations without internal development commands.
- Validate the site with `pnpm build` and `pnpm check` in `docs/`, and check API
  freshness as documented in `docs/contributing/api-reference.md`.
- Commit and push coherent verified milestones, then monitor and repair CI.
  Continue development while CI runs. Before every commit and push, check and record
  previous CI run outcomes, including runs still in progress, and resolve reported
  failures as work proceeds. Do not wait for hosted CI to finish before continuing
  independent work or committing a locally verified milestone.
  Preserve unrelated user changes. Do not publish NuGet packages until the
  faithful port and its required validation are complete.
