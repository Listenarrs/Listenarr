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
        exclude: [...configDefaults.exclude, 'e2e/**', 'cypress/**', 'playwright/**'],
        root: fileURLToPath(new URL('./', import.meta.url)),
      },
    },
  ),
)
