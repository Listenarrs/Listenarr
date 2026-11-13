import { spawnSync } from 'node:child_process'
import { mkdtempSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const frontendDirectory = path.join(repositoryRoot, 'fe')
const apiAssembly = path.join(repositoryRoot, 'listenarr.api', 'bin', 'Release', 'net10.0', 'Listenarr.Api.dll')
const generatedDirectory = path.join(frontendDirectory, 'cypress', 'support', 'generated-api')
const contractOperations = {
  '/api/v1/configuration/apis': ['get'],
  '/api/v1/configuration/bootstrap': ['get'],
  '/api/v1/configuration/settings': ['get'],
  '/api/v1/configuration/startupconfig': ['get'],
  '/api/v1/download-clients': ['get'],
  '/api/v1/downloads': ['get'],
  '/api/v1/filesystem/check-volume': ['get'],
  '/api/v1/filesystem/validate': ['get'],
  '/api/v1/library': ['get'],
  '/api/v1/library/{id}': ['get'],
  '/api/v1/qualityprofile': ['get'],
  '/api/v1/system/health': ['get'],
}
const checkOnly = process.argv.includes('--check')
const noBuild = process.argv.includes('--no-build')

function run(command, args, options = {}) {
  const result = spawnSync(command, args, { stdio: 'inherit', ...options })
  if (result.error) throw result.error
  if (result.status !== 0) throw new Error(`${command} exited with status ${result.status}`)
}

function generatedFiles(directory) {
  return readdirSync(directory).filter(file => file.endsWith('.ts')).sort()
}

const temporaryDirectory = mkdtempSync(path.join(tmpdir(), 'listenarr-api-types-'))

try {
  run('dotnet', ['tool', 'restore'], { cwd: repositoryRoot })
  if (!noBuild) {
    run('dotnet', ['build', 'listenarr.api/Listenarr.Api.csproj', '--configuration', 'Release'], {
      cwd: repositoryRoot,
    })
  }

  const openApiFile = path.join(temporaryDirectory, 'openapi.json')
  const outputDirectory = path.join(temporaryDirectory, 'generated-api')
  const environment = {
    ...process.env,
    ASPNETCORE_ENVIRONMENT: 'Test',
    LISTENARR_TEST_MODE: 'true',
    Listenarr__SqliteDbPath: path.join(temporaryDirectory, 'listenarr.db'),
  }

  run('dotnet', ['swagger', 'tofile', '--output', openApiFile, apiAssembly, 'v1'], {
    cwd: repositoryRoot,
    env: environment,
  })

  const openApi = JSON.parse(readFileSync(openApiFile, 'utf8'))
  const sourceSchemas = openApi.components?.schemas ?? {}
  const selectedPaths = Object.fromEntries(Object.entries(contractOperations).map(([contractPath, methods]) => {
    const pathDefinition = openApi.paths[contractPath]
    const selectedMethods = Object.fromEntries(methods.map(method => {
      if (!pathDefinition?.[method]) throw new Error(`OpenAPI document is missing ${method.toUpperCase()} ${contractPath}`)
      return [method, pathDefinition[method]]
    }))
    return [contractPath, selectedMethods]
  }))
  const selectedSchemaNames = new Set()

  function collectSchemaReferences(value) {
    if (!value || typeof value !== 'object') return
    if (Array.isArray(value)) {
      value.forEach(collectSchemaReferences)
      return
    }

    const reference = value.$ref
    if (typeof reference === 'string' && reference.startsWith('#/components/schemas/')) {
      const schemaName = decodeURIComponent(reference.slice('#/components/schemas/'.length))
      if (!selectedSchemaNames.has(schemaName)) {
        const schema = sourceSchemas[schemaName]
        if (!schema) throw new Error(`OpenAPI document references missing schema ${schemaName}`)
        selectedSchemaNames.add(schemaName)
        collectSchemaReferences(schema)
      }
    }
    Object.values(value).forEach(collectSchemaReferences)
  }

  collectSchemaReferences(selectedPaths)
  openApi.paths = selectedPaths
  openApi.components.schemas = Object.fromEntries(
    [...selectedSchemaNames].sort().map(schemaName => [schemaName, sourceSchemas[schemaName]]),
  )
  writeFileSync(openApiFile, `${JSON.stringify(openApi, null, 2)}\n`)

  run(path.join(frontendDirectory, 'node_modules', '.bin', 'openapi-ts'), [
    '--input', openApiFile,
    '--output', outputDirectory,
    '--plugins', '@hey-api/typescript',
  ], { cwd: frontendDirectory })

  const files = generatedFiles(outputDirectory)
  if (checkOnly) {
    const existingFiles = generatedFiles(generatedDirectory)
    const staleFiles = files.filter(file => {
      try {
        return readFileSync(path.join(outputDirectory, file), 'utf8') !==
          readFileSync(path.join(generatedDirectory, file), 'utf8')
      } catch {
        return true
      }
    })
    const unexpectedFiles = existingFiles.filter(file => !files.includes(file))
    if (staleFiles.length || unexpectedFiles.length) {
      console.error(`Generated API types are stale. Run "npm run api-types:generate" in fe.`)
      if (staleFiles.length) console.error(`Changed or missing: ${staleFiles.join(', ')}`)
      if (unexpectedFiles.length) console.error(`Unexpected generated files: ${unexpectedFiles.join(', ')}`)
      process.exitCode = 1
    } else {
      console.log('Generated API types match the current OpenAPI document.')
    }
  } else {
    mkdirSync(generatedDirectory, { recursive: true })
    for (const file of files) {
      writeFileSync(path.join(generatedDirectory, file), readFileSync(path.join(outputDirectory, file)))
    }
    console.log(`Generated ${files.length} API type files in fe/cypress/support/generated-api.`)
  }
} finally {
  rmSync(temporaryDirectory, { recursive: true, force: true })
}