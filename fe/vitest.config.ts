import { fileURLToPath } from 'node:url'
import { mergeConfig, defineConfig, configDefaults } from 'vitest/config'
import viteConfig from './vite.config'

export default defineConfig((configEnv) =>
  mergeConfig(
    typeof viteConfig === 'function' ? viteConfig(configEnv) : viteConfig,
    {
      resolve: {
        alias: {
          '@': fileURLToPath(new URL('./src', import.meta.url)),
        },
      },
      test: {
        environment: 'jsdom',
        setupFiles: './src/__tests__/test-setup.ts',
        // Keep full-suite runs stable on Windows after dependency updates increase transform load.
        testTimeout: 30000,
        hookTimeout: 30000,
        // Exclude browser test files from unit test runs
        exclude: [...configDefaults.exclude, 'e2e/**', 'playwright/**'],
        root: fileURLToPath(new URL('./', import.meta.url)),
        // `npm run test:coverage`: every app source file, the untested ones at 0 %, in coverage/unit/
        // (lcov.info for the CI aggregate, scripts/test-coverage/aggregate.mjs). Reported, not gated.
        coverage: {
          provider: 'v8',
          include: ['src/**/*.{ts,vue}'],
          exclude: ['src/**/__tests__/**', 'src/**/*.{spec,test}.ts', 'src/**/*.d.ts'],
          reporter: ['text-summary', 'json-summary', 'lcovonly', 'html'],
          reportsDirectory: './coverage/unit',
        },
      },
    },
  ),
)
