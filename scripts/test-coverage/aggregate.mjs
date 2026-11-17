// Merges the coverage every test job wrote into one report per side of the app, frontend and backend.
// Reported, not gated.
//
//   node scripts/test-coverage/aggregate.mjs <input directory> <output directory> [--html]
//
// The input directory holds one folder per test job (in CI, its `coverage-<job>` artifact: manual runs of
// run-tests.yml with the coverage input; coverage is off by default), each with
// any number of reports at any depth:
// - lcov.info: frontend (vitest's coverage/unit/, the Playwright suites' test-results/*-coverage/), with
//   paths relative to fe/ or absolute;
// - *.xml in Cobertura format: backend (coverlet with tests/coverage.runsettings, dotnet-coverage in the E2E
//   test host), whatever the machine's paths (Linux runner, Windows runner, the container's /src).
//
// Both are reduced to lines: a line is coverable when any report lists it and covered when any test hit
// it, so tools that count lines a little differently still merge. Generated code (EF Core migrations,
// source generators' output under obj/) is left out. Writes to the output directory:
// - summary.md (also appended to $GITHUB_STEP_SUMMARY when set) and summary.json: per side, each job's
//   covered lines, its share of the app's coverable lines, the lines no other job covers, and the total;
// - frontend.lcov.info and backend.lcov.info: the merged coverage, paths relative to the repository root
//   (ReportGenerator finds the sources there);
// - with --html, html/index.html from ReportGenerator (`dotnet tool restore` first).
import { spawnSync } from 'node:child_process'
import { appendFile, mkdir, readdir, readFile, writeFile } from 'node:fs/promises'
import { join, relative, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const repositoryRoot = resolve(fileURLToPath(new URL('../..', import.meta.url)))

/** Frontend paths as fe/src/..., from `src/...` (relative to fe/), an absolute path or a URL-like name. */
export function frontendPath(path) {
    const normalized = path.replaceAll('\\', '/')
    const index = normalized.lastIndexOf('fe/src/')
    if (index !== -1) return normalized.slice(index)
    const sourceIndex = normalized.search(/(^|\/)src\//)
    if (sourceIndex === -1) return null
    return `fe/${normalized.slice(sourceIndex).replace(/^\//, '')}`
}

/** Backend paths as listenarr.<layer>/..., wherever the code was built. */
export function backendPath(path) {
    const normalized = path.replaceAll('\\', '/')
    const match = /(?:^|\/)(listenarr\.(?:api|application|domain|infrastructure)\/.+)$/i.exec(
        normalized,
    )
    if (!match) return null
    const file = match[1]
    return /\/(Migrations|obj|bin)\//.test(file) ? null : file
}

/** file -> line -> hits, the higher count when a line appears twice. */
function addLine(files, file, line, hits) {
    const lines = files.get(file) ?? new Map()
    lines.set(line, Math.max(lines.get(line) ?? 0, hits))
    files.set(file, lines)
}

export function parseLcov(text, files = new Map()) {
    let file = null
    for (const rawLine of text.split(/\r?\n/)) {
        if (rawLine.startsWith('SF:')) file = frontendPath(rawLine.slice(3))
        else if (rawLine === 'end_of_record') file = null
        else if (file && rawLine.startsWith('DA:')) {
            const [line, hits] = rawLine.slice(3).split(',')
            addLine(files, file, Number(line), Number(hits))
        }
    }
    return files
}

function attribute(tag, name) {
    return new RegExp(`\\b${name}="([^"]*)"`).exec(tag)?.[1]
}

export function parseCobertura(xml, files = new Map()) {
    for (const [classXml] of xml.matchAll(/<class\b[^>]*>[\s\S]*?<\/class>/g)) {
        const filename = attribute(classXml.slice(0, classXml.indexOf('>') + 1), 'filename')
        const file = filename && backendPath(filename)
        if (!file) continue
        for (const [lineTag] of classXml.matchAll(/<line\b[^>]*>/g)) {
            addLine(
                files,
                file,
                Number(attribute(lineTag, 'number')),
                Number(attribute(lineTag, 'hits')),
            )
        }
    }
    return files
}

async function listFiles(directory) {
    const entries = await readdir(directory, { withFileTypes: true, recursive: true })
    return entries
        .filter((entry) => entry.isFile())
        .map((entry) => join(entry.parentPath ?? entry.path, entry.name))
}

/** The coverage of one job folder: { frontend, backend }, each file -> line -> hits. */
async function readJob(directory) {
    const job = { frontend: new Map(), backend: new Map() }
    for (const file of await listFiles(directory)) {
        if (file.endsWith('lcov.info')) {
            parseLcov(await readFile(file, 'utf8'), job.frontend)
        } else if (file.endsWith('.xml')) {
            const xml = await readFile(file, 'utf8')
            if (/<coverage\b[^>]*\blines-valid=/.test(xml.slice(0, 2000)))
                parseCobertura(xml, job.backend)
        }
    }
    return job
}

function merge(maps) {
    const merged = new Map()
    for (const files of maps) {
        for (const [file, lines] of files) {
            for (const [line, hits] of lines) {
                const target = merged.get(file) ?? new Map()
                target.set(line, (target.get(line) ?? 0) + hits)
                merged.set(file, target)
            }
        }
    }
    return merged
}

function coveredSet(files) {
    const covered = new Set()
    for (const [file, lines] of files) {
        for (const [line, hits] of lines) if (hits > 0) covered.add(`${file}:${line}`)
    }
    return covered
}

function countLines(files) {
    let total = 0
    for (const lines of files.values()) total += lines.size
    return total
}

const percent = (part, whole) => (whole === 0 ? 0 : Math.round((10000 * part) / whole) / 100)

/** One side's numbers: per job, then the total over every job. */
export function summarize(jobs, side) {
    const withSide = jobs.filter((job) => job[side].size > 0)
    const merged = merge(withSide.map((job) => job[side]))
    const total = countLines(merged)
    const coveredByJob = withSide.map((job) => coveredSet(job[side]))
    const rows = withSide.map((job, index) => {
        const covered = coveredByJob[index]
        const others = new Set(
            coveredByJob.filter((_, other) => other !== index).flatMap((set) => [...set]),
        )
        const only = [...covered].filter((line) => !others.has(line)).length
        return {
            job: job.name,
            files: job[side].size,
            covered: covered.size,
            percent: percent(covered.size, total),
            onlyThisJob: only,
        }
    })
    const covered = coveredSet(merged).size
    return {
        side,
        total,
        covered,
        percent: percent(covered, total),
        files: merged.size,
        jobs: rows,
        merged,
    }
}

function markdown(summaries) {
    const sections = summaries.map(({ side, total, covered, percent: pct, files, jobs }) => {
        const title =
            side === 'frontend' ? 'Frontend (fe/src)' : 'Backend (listenarr.*, migrations excluded)'
        if (jobs.length === 0) return `### ${title}\n\nNo coverage was found.\n`
        return [
            `### ${title}`,
            '',
            '| Test job | Lines covered | % of coverable lines | Covered by no other job |',
            '| --- | ---: | ---: | ---: |',
            ...jobs.map(
                (row) =>
                    `| ${row.job} | ${row.covered} | ${row.percent.toFixed(2)} % | ${row.onlyThisJob} |`,
            ),
            `| **All jobs** | **${covered}** | **${pct.toFixed(2)} %** | |`,
            '',
            `${total} coverable lines in ${files} files: a line counts when any job's report lists it.`,
            '',
        ].join('\n')
    })
    return `## Test coverage\n\n${sections.join('\n')}`
}

function lcov(files) {
    const records = [...files.keys()].sort().map((file) => {
        const lines = [...files.get(file)].sort(([a], [b]) => a - b)
        const hit = lines.filter(([, hits]) => hits > 0).length
        return [
            `SF:${file}`,
            ...lines.map(([line, hits]) => `DA:${line},${hits}`),
            `LF:${lines.length}`,
            `LH:${hit}`,
            'end_of_record',
        ].join('\n')
    })
    return `TN:\n${records.join('\n')}\n`
}

async function main() {
    const args = process.argv.slice(2)
    const html = args.includes('--html')
    const [inputDirectory, outputDirectory] = args.filter((arg) => arg !== '--html')
    if (!inputDirectory || !outputDirectory) {
        throw new Error(
            'Usage: node scripts/test-coverage/aggregate.mjs <input directory> <output directory> [--html]',
        )
    }

    const folders = (await readdir(inputDirectory, { withFileTypes: true }))
        .filter((entry) => entry.isDirectory())
        .map((entry) => entry.name)
        .sort()
    const jobs = []
    for (const folder of folders) {
        const job = await readJob(join(inputDirectory, folder))
        jobs.push({ name: folder.replace(/^coverage-/, ''), ...job })
    }

    const summaries = ['frontend', 'backend'].map((side) => summarize(jobs, side))
    await mkdir(outputDirectory, { recursive: true })
    const reports = []
    for (const summary of summaries) {
        if (summary.files === 0) continue
        const path = join(outputDirectory, `${summary.side}.lcov.info`)
        await writeFile(path, lcov(summary.merged))
        reports.push(path)
    }
    const text = markdown(summaries)
    await writeFile(join(outputDirectory, 'summary.md'), text)
    await writeFile(
        join(outputDirectory, 'summary.json'),
        JSON.stringify(
            summaries.map(({ merged: _merged, ...summary }) => summary),
            null,
            2,
        ),
    )
    if (process.env.GITHUB_STEP_SUMMARY) await appendFile(process.env.GITHUB_STEP_SUMMARY, text)
    console.log(text)

    if (html && reports.length > 0) {
        const result = spawnSync(
            'dotnet',
            [
                'tool',
                'run',
                'reportgenerator',
                `-reports:${reports.join(';')}`,
                `-targetdir:${join(outputDirectory, 'html')}`,
                '-reporttypes:Html',
                `-sourcedirs:${repositoryRoot}`,
                '-title:Listenarr test coverage (all test jobs)',
            ],
            { cwd: repositoryRoot, stdio: 'inherit' },
        )
        if (result.status !== 0) throw new Error('ReportGenerator failed.')
        console.log(
            `HTML report: ${relative(process.cwd(), join(outputDirectory, 'html', 'index.html'))}`,
        )
    }
}

if (process.argv[1] === fileURLToPath(import.meta.url)) await main()
