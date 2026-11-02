import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { buildTokens } from './token-builder.mjs';
import { mapTypographyToken } from './tailwind-theme-mapper.mjs';

const repository = fileURLToPath(new URL('../', import.meta.url));

test('typography tracking survives archive ingestion, runtime CSS and Tailwind for both products', async () => {
  const workspace = fs.mkdtempSync(path.join(os.tmpdir(), 'mycelium-tracking-test-'));
  try {
    const tokensDirectory = path.join(workspace, 'tokens');
    execFileSync('dotnet', ['run', '--file', path.join(repository, '.github/scripts/design-tools.cs'), '--',
      'ingest', path.join(repository, 'Ressources/Mycelium.Design.DTCG.zip'), tokensDirectory]);
    const source = JSON.parse(fs.readFileSync(path.join(tokensDirectory, 'shared/typography.json'), 'utf8'));
    const typography = source.typography.typography;
    const cases = [['display', 'lg', -0.25], ['label', 'sm', 0.1], ['label', 'xs', 0.5], ['body', 'md', 0]];
    for (const [group, key, expected] of cases) {
      assert.equal(typography[group][key].$value.letterSpacing.value, expected);
    }
    for (const product of ['bloom', 'forge']) {
      const outputDirectory = path.join(workspace, product);
      await buildTokens({ target: product, tokensDirectory, outputDirectory });
      assert.deepEqual(fs.readdirSync(outputDirectory).sort(), ['theme-mycelium.css', `tokens-${product}.css`]);
      const runtime = fs.readFileSync(path.join(outputDirectory, `tokens-${product}.css`), 'utf8');
      const theme = fs.readFileSync(path.join(outputDirectory, 'theme-mycelium.css'), 'utf8');
      const blocks = [...runtime.matchAll(/(?:^|\n)(:root|\.dark) \{([^}]+)\}/g)];
      assert.equal(blocks.length, 2);
      for (const [group, key, expected] of cases) {
        const scale = `${group}-${key}`;
        for (const [, , block] of blocks) {
          assert.ok(block.includes(`--typography-typography-${scale}-letter-spacing: ${expected}px;`));
        }
        assert.ok(theme.includes(`--tracking-${scale}: var(--typography-typography-${scale}-letter-spacing);`));
      }
      // All pre-existing runtime declarations and Tailwind mappings remain compatible.
      for (const [file, generated] of [[`tokens-${product}.css`, runtime], ['theme-mycelium.css', theme]]) {
        const previous = fs.readFileSync(path.join(repository, `Mycelium.Design.${product === 'bloom' ? 'Bloom' : 'Forge'}/css`, file), 'utf8');
        for (const [declaration] of previous.matchAll(/^\s*--[^:\n]+:[^;\n]*;/gm)) {
          assert.ok(generated.includes(declaration), `Lost declaration: ${declaration}`);
        }
        assert.ok(generated.startsWith('/**\n * Do not edit directly, this file was auto-generated.'));
        assert.doesNotMatch(generated, /undefined|\[object Object\]|NaN|:\s*\{[^}]+\};/);
      }
      const tracking = [...theme.matchAll(/--tracking-([^:]+):/g)].map(match => match[1]);
      assert.equal(new Set(tracking).size, tracking.length);
      await buildTokens({ target: product, tokensDirectory, outputDirectory });
      assert.equal(fs.readFileSync(path.join(outputDirectory, `tokens-${product}.css`), 'utf8'), runtime);
      assert.equal(fs.readFileSync(path.join(outputDirectory, 'theme-mycelium.css'), 'utf8'), theme);
    }
  } finally {
    fs.rmSync(workspace, { recursive: true, force: true });
  }
});

test('tracking is optional and zero is retained without adding text subproperties', () => {
  const token = { name: 'typography-typography-body-md', path: ['typography', 'typography', 'body', 'md'],
    $value: { fontSize: { value: 14, unit: 'px' }, lineHeight: { value: 20, unit: 'px' }, fontWeight: 400, fontFamily: 'Inter' } };
  const existing = mapTypographyToken(token, '');
  assert.equal(existing.length, 4);
  assert.deepEqual(mapTypographyToken({ ...token, $value: { ...token.$value, letterSpacing: { value: 0, unit: 'px' } } }, ''),
    ['  --tracking-body-md: var(--typography-typography-body-md-letter-spacing);', ...existing]);
});
