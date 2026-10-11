---
title: Ankus
description: Write PostgreSQL extensions in C# with .NET Native AOT.
template: splash
head:
  - tag: title
    content: Ankus
hero:
  tagline: PostgreSQL extensions in C#, built with .NET Native AOT
  actions:
    - text: Get started
      link: ./getting-started/functions/
      icon: right-arrow
---

```csharp
[PgFunction]
public static int Add(int a, int b)
    => checked(a + b);
```

```sql
SELECT add(40, 2);  -- 42
```

<div class="feature-strip">
  <div class="feature-item">
    <span class="feature-key">[PgFunction]</span>
    <span class="feature-label">Attributes declare functions, aggregates, operators, casts, triggers and types. A source generator writes the SQL and the C entry points.</span>
  </div>
  <div class="feature-item">
    <span class="feature-key">Native AOT</span>
    <span class="feature-label">Each extension is one native library. The server needs no .NET installation.</span>
  </div>
  <div class="feature-item">
    <span class="feature-key">PgException</span>
    <span class="feature-label">PostgreSQL errors never unwind through managed code. They reach you as exceptions you can catch.</span>
  </div>
  <div class="feature-item">
    <span class="feature-key">Spi.Sql</span>
    <span class="feature-label">Query from inside the backend. Interpolated values become bound parameters, not SQL text.</span>
  </div>
  <div class="feature-item">
    <span class="feature-key">ankus test</span>
    <span class="feature-label">Tests run inside a real server and roll back, as pgrx tests do. Add --all for every registered version.</span>
  </div>
</div>

<p class="platform-note">PostgreSQL 13 through 18 and 19 beta on Linux, macOS and Windows</p>
