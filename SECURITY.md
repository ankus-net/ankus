# Security

Report a vulnerability privately through GitHub Security Advisories for the `ankus-net/ankus` repository: open the
Security tab and choose "Report a vulnerability". Please do not open a public issue for one. Fixes go into `main`
and ship in the next release.

## What to report

An Ankus extension runs inside PostgreSQL server processes, with the server's operating-system account, as a C
extension does. Code an extension author writes that does what it says is working as intended. A report is welcome
when Ankus itself does something the author did not ask for, for example when:

- a PostgreSQL error, cancellation, signal or process exit corrupts managed state, crashes the server, or exposes one
  session's data to another;
- generated SQL, or a value bound through `Spi.Sql` interpolation, is run as SQL text;
- an API skips a privilege or trust check it documents, such as a relation writer's table privileges and row-level
  security, or PostgreSQL's `output_plugin_libraries`;
- the build, the `ankus` tool or an installation runs or loads something other than what it names, or exposes a
  credential.

## The embedded .NET runtime

Each extension embeds Ankus's patched .NET Native AOT runtime, so a .NET security release reaches an extension only
when it is rebuilt with an Ankus release that carries that servicing update. Rebuild and redeploy your extensions after
each Ankus servicing release. [.NET support](https://ankus.net/reference/dotnet-support/) explains how Ankus
follows .NET servicing.

## Network use

Ankus collects no telemetry. The `ankus` tool downloads PostgreSQL only when asked, as with `ankus init --pg18
download`, and restores packages through your own NuGet configuration. A replication connection reaches only the
server an extension names.
