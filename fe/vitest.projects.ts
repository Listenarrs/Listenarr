import { fileURLToPath } from 'node:url'
import { configDefaults, type TestProjectConfiguration } from 'vitest/config'

export const testRoot = fileURLToPath(new URL('./', import.meta.url))

export const testExclude = [...configDefaults.exclude, 'e2e/**', 'cypress/**']

export const jsdomEnvironment = {
  environment: 'jsdom' as const,
  environmentOptions: {
    jsdom: {
      url: 'http://localhost/',
    },
  },
}

export const jsdomTestGlobs = ['src/**/test/**/*.spec.ts']

export const nodeTestGlobs = ['src/**/test/**/*.node.spec.ts']

export const smokeTestGlobs = ['src/test/smoke/**/*.spec.ts']

export const testProjects: TestProjectConfiguration[] = [
  {
    extends: true,
    test: {
      name: 'unit-node',
      environment: 'node',
      include: nodeTestGlobs,
    },
  },
  {
    extends: true,
    test: {
      ...jsdomEnvironment,
      name: 'unit-jsdom',
      include: jsdomTestGlobs,
      exclude: [...testExclude, ...nodeTestGlobs, ...smokeTestGlobs],
    },
  },
  {
    extends: true,
    test: {
      ...jsdomEnvironment,
      name: 'smoke',
      include: smokeTestGlobs,
    },
  },
]
