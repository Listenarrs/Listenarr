import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'
import {
  findUnsafeRepositoryPaths,
  isUnsafeRepositorySegment
} from './validate-repository-paths.mjs'

const reservedDeviceFixture = JSON.parse(
  readFileSync(
    new URL('../test-fixtures/windows-reserved-device-names.json', import.meta.url),
    'utf8'
  )
)

test('rejects every name in the shared Windows reserved-device fixture', () => {
  for (const name of reservedDeviceFixture.reserved) {
    assert.equal(isUnsafeRepositorySegment(`${name}.folder`), true, name)
  }
})

test('accepts every nonreserved name in the shared fixture', () => {
  for (const name of reservedDeviceFixture.nonReserved) {
    assert.equal(isUnsafeRepositorySegment(`${name}.folder`), false, name)
  }
})

test('rejects Windows-invalid characters in repository segments', () => {
  for (const name of [
    'bad:name.txt',
    'bad<name>.txt',
    'bad|name.txt',
    'bad?name.txt',
    'bad*name.txt',
    'bad\\name.txt',
    `bad${String.fromCharCode(1)}name.txt`
  ]) {
    assert.equal(isUnsafeRepositorySegment(name), true, name)
  }
})

test('rejects hazardous trailing dots and spaces in any segment', () => {
  assert.deepEqual(
    findUnsafeRepositoryPaths([
      'safe/file.txt',
      'unsafe./file.txt',
      'safe/bad-name ',
      'nested/NUL.txt/file'
    ]),
    ['unsafe./file.txt', 'safe/bad-name ', 'nested/NUL.txt/file']
  )
})

test('keeps repository path validation wired to publication gates', () => {
  const preCommit = readFileSync(
    new URL('../.husky/pre-commit', import.meta.url),
    'utf8'
  )
  const prePush = readFileSync(
    new URL('../.husky/pre-push', import.meta.url),
    'utf8'
  )
  const workflow = readFileSync(
    new URL('../.github/workflows/run-tests.yml', import.meta.url),
    'utf8'
  )

  assert.match(preCommit, /node scripts\/lint-staged\.mjs/u)
  assert.match(prePush, /node scripts\/validate-repository-paths\.mjs/u)
  assert.match(workflow, /npm run --silent validate:repository-paths/u)
  assert.match(
    readFileSync(new URL('../scripts/lint-staged.mjs', import.meta.url), 'utf8'),
    /run\('node', \['scripts\/validate-repository-paths\.mjs'\]\)/u
  )
})
