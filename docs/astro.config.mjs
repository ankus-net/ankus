import { defineConfig } from 'astro/config';
import { satteri } from '@astrojs/markdown-satteri';
import starlight from '@astrojs/starlight';
import { documentationGrammars } from './src/syntax/grammars.mjs';

export default defineConfig({
  site: 'https://ankus.net',
  outDir: '../artifacts/docs',
  cacheDir: './node_modules/.astro-cache',
  markdown: {
    processor: satteri(),
  },
  integrations: [
    starlight({
      title: 'Ankus',
      description: 'PostgreSQL extensions in C# with .NET Native AOT.',
      logo: { light: './src/assets/logo-light.svg', dark: './src/assets/logo-dark.svg', alt: 'Ankus' },
      favicon: '/favicon.svg',
      head: [
        { tag: 'link', attrs: { rel: 'icon', type: 'image/x-icon', sizes: '16x16 32x32 48x48', href: '/favicon.ico' } },
        { tag: 'link', attrs: { rel: 'icon', type: 'image/png', sizes: '16x16', href: '/favicon-16.png' } },
        { tag: 'link', attrs: { rel: 'apple-touch-icon', sizes: '180x180', href: '/apple-touch-icon.png' } },
      ],
      disable404Route: true,
      social: [
        {
          icon: 'github',
          label: 'GitHub',
          href: 'https://github.com/ankus-net/ankus',
        },
      ],
      customCss: ['./src/styles/custom.css'],
      expressiveCode: {
        shiki: { langs: documentationGrammars },
        customizeTheme(theme) {
          // The bundled themes omit C# type and variable scopes, including attributes and generics.
          theme.settings.push(
            {
              scope: ['source.cs entity.name.type', 'source.cs support.type', 'source.cs support.class'],
              settings: { foreground: theme.type === 'dark' ? '#7FDBCA' : '#006B60' },
            },
            {
              scope: [
                'source.cs entity.name.variable',
                'source.cs variable',
                'variable.other.readwrite.cs',
                'variable.other.object.cs',
              ],
              settings: { foreground: theme.type === 'dark' ? '#9CDCFE' : '#234A97' },
            },
          );
        },
      },
      sidebar: [
        {
          label: 'Getting started',
          items: [
            { label: 'Write a function', slug: 'getting-started/functions' },
            { label: 'Test an extension', slug: 'getting-started/testing' },
            { label: 'Publish and install', slug: 'getting-started/publishing' },
            { label: 'Coming from pgrx', slug: 'getting-started/from-pgrx' },
          ],
        },
        {
          label: 'Working with PostgreSQL',
          items: [
            { label: 'Function declarations', slug: 'function-declarations' },
            { label: 'Calling PostgreSQL functions', slug: 'calling-functions' },
            { label: 'Catalog lookups and OIDs', slug: 'catalog-lookups' },
            { label: 'Relation access', slug: 'relations' },
            { label: 'Extension initialization', slug: 'initialization' },
            { label: 'Background workers', slug: 'background-workers' },
            { label: 'Configuration settings', slug: 'configuration' },
            { label: 'Sets and tables', slug: 'sets-and-tables' },
            { label: 'Composite values', slug: 'composites' },
            { label: 'Custom types', slug: 'custom-types' },
            { label: 'Triggers', slug: 'triggers' },
            { label: 'Event triggers', slug: 'event-triggers' },
            { label: 'Aggregates', slug: 'aggregates' },
            { label: 'Operators and casts', slug: 'operators-and-casts' },
            { label: 'Custom SQL', slug: 'custom-sql' },
            { label: 'SPI queries', slug: 'spi' },
            { label: 'Transaction IDs', slug: 'transaction-ids' },
            { label: 'Tuple locations', slug: 'item-pointers' },
            { label: 'Transaction callbacks', slug: 'transaction-callbacks' },
            { label: 'Memory contexts', slug: 'memory-contexts' },
            { label: 'Shared memory and locks', slug: 'shared-memory' },
            { label: 'StringInfo buffers', slug: 'stringinfo' },
            { label: 'Text and binary values', slug: 'text-and-binary' },
            { label: 'PostgreSQL lists', slug: 'lists' },
            { label: 'Arrays', slug: 'arrays' },
            { label: 'Polymorphic values', slug: 'polymorphic-values' },
            { label: 'Raw values and custom types', slug: 'raw-values' },
            { label: 'Custom scan providers', slug: 'custom-scans' },
            { label: 'Logical decoding output plugins', slug: 'logical-decoding' },
            { label: 'Replication connections', slug: 'replication-connections' },
            { label: 'Applying replicated changes', slug: 'applying-changes' },
            { label: 'Internal state', slug: 'internal-state' },
            { label: 'JSON and UUID values', slug: 'json-and-uuid' },
            { label: 'Numeric values', slug: 'numeric' },
            { label: 'Date and time values', slug: 'date-and-time' },
            { label: 'Network values', slug: 'network' },
            { label: 'Geometric values', slug: 'geometry' },
            { label: 'Ranges', slug: 'ranges' },
            { label: 'Enumerated types', slug: 'enums' },
            { label: 'Logging and errors', slug: 'logging' },
            { label: 'Benchmarks', slug: 'benchmarks' },
          ],
        },
        {
          label: 'Reference',
          items: [
            { label: 'Command-line tool', slug: 'reference/cli' },
            { label: 'Build settings', slug: 'reference/build-settings' },
            { label: '.NET support', slug: 'reference/dotnet-support' },
            { label: 'Running .NET in PostgreSQL', slug: 'reference/execution' },
            { label: 'Boundary costs', slug: 'reference/performance' },
            { label: 'pgrx examples', slug: 'reference/pgrx-examples' },
          ],
        },
        {
          label: 'API reference',
          items: [
            { label: 'Overview', slug: 'api' },
            { autogenerate: { directory: 'api/generated' } },
          ],
        },
      ],
    }),
  ],
});
