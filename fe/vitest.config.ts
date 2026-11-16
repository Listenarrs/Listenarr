import { fileURLToPath } from 'node:url'
import { mergeConfig, defineConfig, configDefaults } from 'vitest/config'
import viteConfig from './vite.config'
import { testProjects, testRoot } from './vitest.projects'

export default defineConfig((configEnv) =>
  mergeConfig(
    typeof viteConfig === 'function' ? viteConfig(configEnv) : viteConfig,
    {
      oxc: {
        tsconfig: false,
      },
      resolve: {
        alias: {
          '@': fileURLToPath(new URL('./src', import.meta.url)),
        },
      },
      test: {
        execArgv: ['--no-warnings'],
        setupFiles: ['src/test/setup/signalr.ts'],
        projects: testProjects,
        // Keep full-suite runs stable on Windows after dependency updates increase transform load.
        testTimeout: 30000,
        hookTimeout: 30000,
        // Exclude e2e and cypress test files from unit test runs
        exclude: [...configDefaults.exclude, 'e2e/**', 'cypress/**'],
        root: testRoot,
        coverage: {
          provider: 'v8',
          reportsDirectory: 'coverage/unit',
          reporter: ['text', 'html', 'lcov'],
          include: ['src/**/*.{ts,vue}'],
          exclude: [
            ...configDefaults.coverage.exclude,
            'src/**/test/**',
            'src/test/**',
            'src/**/*.d.ts',
            'src/env.d.ts',
            'src/main.ts',
          ],
          thresholds: {
            branches: 30,
            functions: 30,
            lines: 40,
            statements: 40,
          },
        },
      },
    },
  ),
)
