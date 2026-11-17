// Converts the videos Playwright keeps for failed E2E tests (video: 'retain-on-failure') into animated
// GIFs, so a failure leaves a shareable artifact next to its webm. scripts/e2e/run.mjs calls
// convertVideos() on fe/test-results/e2e after the suite; a missing ffmpeg or a failed conversion is
// logged and skipped, never failing the run (the suite's own exit code gates it).
import { spawn } from 'node:child_process'
import { readdir } from 'node:fs/promises'
import { join } from 'node:path'

// Two-pass palette (generate, then apply) for a clean GIF; 10 fps and a 1000px width keep it small.
const FILTERGRAPH =
  'fps=10,scale=1000:-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle'

export function ffmpegGifArgs(input, output) {
  return ['-y', '-loglevel', 'error', '-i', input, '-filter_complex', FILTERGRAPH, output]
}

export async function findVideos(dir) {
  let entries
  try {
    entries = await readdir(dir, { withFileTypes: true })
  } catch (error) {
    if (error.code === 'ENOENT') return []
    throw error
  }
  const videos = []
  for (const entry of entries) {
    const full = join(dir, entry.name)
    if (entry.isDirectory()) videos.push(...(await findVideos(full)))
    else if (entry.name === 'video.webm') videos.push(full)
  }
  return videos
}

// events.once() rejects on an 'error' event, which would make a missing ffmpeg throw instead of being
// skipped. Resolve on both events and return the outcome to the caller.
function ffmpegResult(child) {
  return new Promise((resolve) => {
    child.once('error', resolve)
    child.once('close', resolve)
  })
}

async function convertOne(input) {
  const output = input.replace(/\.webm$/, '.gif')
  const ffmpeg = spawn('ffmpeg', ffmpegGifArgs(input, output), { stdio: ['ignore', 'ignore', 'inherit'] })
  const result = await ffmpegResult(ffmpeg)
  if (result instanceof Error) {
    console.warn(`Could not run ffmpeg to make a GIF from ${input}: ${result.message}`)
    return null
  }
  if (result !== 0) {
    console.warn(`ffmpeg exited with code ${result} converting ${input} to a GIF.`)
    return null
  }
  return output
}

export async function convertVideos(dir) {
  const gifs = []
  for (const video of await findVideos(dir)) {
    const gif = await convertOne(video)
    if (gif) {
      gifs.push(gif)
      console.log(`Wrote failure GIF ${gif}`)
    }
  }
  return gifs
}
