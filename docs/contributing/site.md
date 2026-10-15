# Documentation site

Use Node.js 24, pnpm 10.28.0, and the .NET SDK selected by `global.json`. From `docs/`:

```console
pnpm install --frozen-lockfile
pnpm dev
```

The development server prints its local address. To check and build the static site:

```console
pnpm check
pnpm build
```

The output goes to `../artifacts/docs`. `pnpm preview` serves that build, including
the search index. Search is generated during the production build.

`pnpm build` also regenerates the [public API reference](api-reference.md) from
the compiled XML documentation. Run `pnpm api` to regenerate only those pages.

The site is published at the root of `https://ankus.net`, the custom domain of the
repository's GitHub Pages, so pages link each other by root-relative routes with no
base path. Astro's generated `docs/.astro/` files are ignored by Git.

Pages in `src/content/docs/` are for people writing and deploying extensions.
Keep examples close to the behavior they explain. Prefer a short explanation
followed by code; add details when they change how the API should be used.
Repository development, testing, and internal design notes belong in
`docs/contributing/`. Implementation status belongs in the root `PROGRESS.md`.

Site pages link each other by their published routes, such as `/api/ankus/`,
which do not resolve when a page's source is viewed on GitHub. The README,
`SECURITY.md` and sample READMEs therefore link the published pages under
`https://ankus.net/`; contributor documents may link a page's
source when they are about editing it.

The site uses Astro and Starlight. TypeScript is pinned to the newest stable
version supported by `@astrojs/check`.
