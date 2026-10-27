import assert from 'node:assert/strict'
import { mkdtemp, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { test } from 'node:test'
import { fileURLToPath } from 'node:url'
import { build } from 'vite'

for (const mode of ['production', 'analysis']) {
  test(`${mode} build handles debug output correctly`, async () => {
    const root = await mkdtemp(join(tmpdir(), 'listenarr-build-'))
    try {
      const entry = join(root, 'entry.js')
      await writeFile(
        entry,
        'console.log("listenarr-debug-marker"); debugger; window.listenarrBuildControl = "kept";',
      )
      const result = await build({
        configFile: fileURLToPath(new URL('../vite.config.ts', import.meta.url)),
        root,
        mode,
        logLevel: 'silent',
        build: { write: false, rolldownOptions: { input: entry } },
      })
      const outputs = Array.isArray(result) ? result : [result]
      const code = outputs
        .flatMap((output) => output.output)
        .filter((output) => output.type === 'chunk')
        .map((output) => output.code)
        .join('\n')

      assert.ok(code.includes('listenarrBuildControl'))
      assert.equal(code.includes('listenarr-debug-marker'), mode === 'analysis')
      if (mode === 'production') assert.doesNotMatch(code, /\bdebugger\b/)
    } finally {
      await rm(root, { recursive: true, force: true })
    }
  })
}
