// MSTest's package adds this namespace through MSBuild. Stating it here lets tools that read only the sources, such as
// CodeQL's C# scan, resolve the test framework too.
global using Microsoft.VisualStudio.TestTools.UnitTesting;
