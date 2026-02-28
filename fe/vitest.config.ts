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
        // Increase global test timeout to reduce flaky timeouts in CI/local runs
        testTimeout: 10000,
        // Exclude e2e and cypress test files from unit test runs
        exclude: [...configDefaults.exclude, 'e2e/**', 'cypress/**'],
        root: fileURLToPath(new URL('./', import.meta.url)),
      },
    },
  ),
)
