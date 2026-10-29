# Ankus

Write PostgreSQL extensions in C# and publish them as .NET Native AOT shared libraries.
Ankus generates native entry points, SQL declarations, conversions, and error boundaries.

## Extension projects

Create an extension solution with managed and PostgreSQL tests:

```console
ankus new Hello
cd Hello
dotnet test
```

The generated solution uses Central Package Management and the same Ankus
version as the installed tool. Backend tests run in an isolated PostgreSQL cluster
for the selected major, 18 by default.

Use `Ankus.Sdk` as the project SDK:

```xml
<Project Sdk="Ankus.Sdk/0.2.17">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AnkusExtensionName>hello</AnkusExtensionName>
    <AnkusExtensionVersion>0.1.0</AnkusExtensionVersion>
  </PropertyGroup>
</Project>
```

```csharp
using Ankus;

public static class Functions
{
    [PgFunction]
    public static int Add(int left, int right) => checked(left + right);
}
```

The SDK enables Native AOT and references matching `Ankus.Runtime` and
`Ankus.Generators` packages. Building requires .NET 10 and its Native AOT toolchain;
publishing also requires PostgreSQL server development headers.

```console
dotnet publish -c Release -r linux-x64 -p:AnkusPgConfigPath=/path/to/pg_config
```

The result contains a native library, SQL, PostgreSQL extension control files, and
`ankus.extension.json`. The PostgreSQL host does not need an installed .NET runtime.
Use the runtime identifier for the machine running PostgreSQL and set
`AnkusPostgresMajor` when targeting a major version other than 18.

## Packages

| Package | Purpose |
|---|---|
| `Ankus.Sdk` | Project SDK, Native AOT publishing, native compilation, and SQL output |
| `Ankus.Runtime` | Function attributes, PostgreSQL values, SPI, and diagnostics |
| `Ankus.Generators` | C# analyzer/source generator; included by the SDK |
| `Ankus.PgConfig` | PostgreSQL installation discovery, registration, and artifact manifests |
| `Ankus.Testing` | Isolated PostgreSQL clusters for ordinary .NET test projects |
| `Ankus.Tool` | The `ankus` .NET tool: project creation, registration, publishing, and installation |
| `Ankus.Templates` | `dotnet new` templates for extensions and background workers |
| `Ankus.NativeAot.Runtime.<rid>`, `Ankus.NativeAot.Compiler.<rid>` | The patched Native AOT runtime and compiler for PostgreSQL process and preload behavior; the SDK restores the ones for your build machine and target |

Install `Ankus.Tool` with `dotnet tool install --global Ankus.Tool --version 0.2.17`. Register PostgreSQL with
`ankus init --pg18 /path/to/pg_config`, then run `ankus publish` and
`ankus install --from /path/to/publish`.
