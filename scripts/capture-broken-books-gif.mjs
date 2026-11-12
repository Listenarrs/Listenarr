// Run with E2E_TARGET=ct105 and OUTPUT=<gif path>; restores the seeded CT 105 snapshot afterward.
import { homedir } from 'node:os'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'
import { once } from 'node:events'
import { spawn } from 'node:child_process'

if (process.env.E2E_TARGET !== 'ct105') {
  throw new Error('Set E2E_TARGET=ct105; this capture must not use a real Listenarr instance.')
}

if (!process.env.OUTPUT) throw new Error('Set OUTPUT to the destination GIF path.')

const e2e = join(homedir(), '.cache/listenarr-dev/e2e')
const [{ api, browser: launchBrowser, page: openPage, BASE }, { hasSnapshot, restore }, helpers] =
  await Promise.all([
    import(pathToFileURL(join(e2e, 'lib.mjs'))),
    import(pathToFileURL(join(e2e, 'instance.mjs'))),
    import(pathToFileURL(join(e2e, 'helpers.mjs'))),
  ])

const { matchCard, repairsOver, resultStates, startRepair, testId } = helpers

if (!hasSnapshot('seeded')) throw new Error('CT 105 needs its seeded E2E snapshot.')

const targetId = 3
const remainingId = 2
const targetTitle = 'Twenty Thousand Leagues Under the Sea'
const rightRecord = {
  author: 'Jules Verne',
  pick: 'B0F48KS3BX',
  region: 'fr',
  title: 'Vingt mille lieues sous les mers',
}

const ffmpeg = spawn(
  'ffmpeg',
  [
    '-y',
    '-f',
    'image2pipe',
    '-framerate',
    '12',
    '-vcodec',
    'mjpeg',
    '-i',
    'pipe:0',
    '-filter_complex',
    '[0:v]fps=12,scale=1100:-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle',
    '-loop',
    '0',
    process.env.OUTPUT,
  ],
  { stdio: ['pipe', 'ignore', 'inherit'] },
)
const ffmpegClosed = once(ffmpeg, 'close')
let browser
let recordingFinished = false

async function replaceRegionWithMissing(audiobookId) {
  const { identifiers } = await api('GET', `library/${audiobookId}/identifiers`)
  const updated = identifiers.map((identifier) => ({
    type: identifier.type,
    value: identifier.value,
    region: identifier.type === 'Asin' ? null : identifier.region,
    isPrimary: identifier.isPrimary,
    source: identifier.source,
  }))
  await api('PUT', `library/${audiobookId}/identifiers`, { identifiers: updated })
}

async function recordFrames(page, durationMs) {
  const frames = Math.ceil(durationMs / 83)
  for (let frame = 0; frame < frames; frame += 1) {
    if (!ffmpeg.stdin.write(await page.screenshot({ type: 'jpeg', quality: 82 }))) {
      await once(ffmpeg.stdin, 'drain')
    }
    await page.waitForTimeout(83)
  }
}

async function addVisibleCursor(page) {
  await page.evaluate(() => {
    const cursor = document.createElement('div')
    cursor.id = 'demo-cursor'
    cursor.innerHTML =
      '<svg viewBox="0 0 24 32" aria-hidden="true"><path d="M2 1v25l6.5-6.1 4.2 9.1 4-1.8-4.2-9.1 9-.1L2 1Z" fill="#fff" stroke="#111" stroke-width="2" stroke-linejoin="round"/></svg>'
    document.body.append(cursor)

    document.addEventListener('mousemove', (event) => {
      cursor.style.transform = `translate3d(${event.clientX}px, ${event.clientY}px, 0)`
    })
    document.addEventListener('mousedown', () => cursor.classList.add('clicked'))
    document.addEventListener('mouseup', () => cursor.classList.remove('clicked'))
  })
  await page.addStyleTag({
    content: `
      * { cursor: none !important; }
      #demo-cursor { position: fixed; left: 0; top: 0; z-index: 2147483647; width: 24px; height: 32px; pointer-events: none; filter: drop-shadow(1px 2px 2px #000); }
      #demo-cursor svg { display: block; width: 24px; height: 32px; }
      #demo-cursor::after { content: ''; position: absolute; left: -7px; top: -7px; width: 36px; height: 36px; border: 2px solid #ffd54a; border-radius: 50%; opacity: 0; transform: scale(.5); transition: opacity .12s, transform .12s; }
      #demo-cursor.clicked::after { opacity: .9; transform: scale(1); }
    `,
  })
}

try {
  await restore('seeded')
  await replaceRegionWithMissing(targetId)
  await replaceRegionWithMissing(remainingId)

  const initialBroken = (await api('GET', 'library')).filter((book) => book.brokenReasons?.length)
  if (initialBroken.length !== 2) throw new Error(`Expected two broken books before repair, found ${initialBroken.length}.`)

  browser = await launchBrowser()
  const page = await openPage(browser, { width: 1600, height: 900 })
  await page.goto(`${BASE}/audiobooks`, { waitUntil: 'networkidle' })
  await page.locator('.group-btn').waitFor({ state: 'visible' })
  const securityWarningDismiss = page.locator('.security-warning-dismiss')
  if (await securityWarningDismiss.isVisible()) await securityWarningDismiss.click()
  await addVisibleCursor(page)

  const ffmpegError = once(ffmpeg, 'error')
  ffmpegError.catch(() => {})
  await recordFrames(page, 1200)
  await page.locator('.group-btn').click()
  await page.locator('.group-menu').waitFor({ state: 'visible' })
  await recordFrames(page, 900)
  await testId(page, 'group-broken').click()
  await page.getByRole('button', { name: 'Broken books' }).waitFor({ state: 'visible' })
  await page.locator('.audiobook-poster-container').nth(1).waitFor({ state: 'visible' })
  if (await page.locator('.audiobook-poster-container').count() !== 2) {
    throw new Error('The Broken books filter did not show exactly two books.')
  }
  if (await testId(page, 'broken-reasons').count() !== 2) {
    throw new Error('Expected both broken books to display their reasons.')
  }
  await recordFrames(page, 1700)

  const targetCard = page.locator('.audiobook-item').filter({ hasText: targetTitle })
  await targetCard.locator('.selection-checkbox').click()
  const repairSelected = page.getByRole('button', { name: 'Repair Selected (1)' })
  await repairSelected.waitFor({ state: 'visible' })
  await recordFrames(page, 800)
  await repairSelected.click()
  await testId(page, `repair-row-${targetId}`).waitFor({ state: 'visible' })
  await recordFrames(page, 1000)

  await matchCard(page, targetId, rightRecord)
  await testId(page, 'repair-confirm').filter({ hasText: 'Repair 1' }).waitFor()
  await recordFrames(page, 1000)
  await startRepair(page)
  await repairsOver(page)
  if (JSON.stringify(await resultStates(page, [targetId])) !== JSON.stringify(['done'])) {
    throw new Error('The selected book did not finish its repair successfully.')
  }
  await recordFrames(page, 2400)

  const afterRepair = await api('GET', 'library')
  const targetAfterRepair = afterRepair.find((book) => book.id === targetId)
  const remainingAfterRepair = afterRepair.filter((book) => book.brokenReasons?.length)
  if (targetAfterRepair?.brokenReasons?.length) throw new Error('The repaired book is still broken.')
  if (remainingAfterRepair.length !== 1 || remainingAfterRepair[0].id !== remainingId) {
    throw new Error('Expected only the unselected book to remain broken after repair.')
  }

  await testId(page, 'repair-cancel').click()
  await testId(page, 'repair-confirm').waitFor({ state: 'visible' })
  await testId(page, 'repair-cancel').click()
  await page.locator('.modal-overlay').waitFor({ state: 'detached' })
  await page.goto(`${BASE}/audiobooks?group=broken`, { waitUntil: 'networkidle' })
  await page.locator('.audiobook-poster-container').first().waitFor({ state: 'visible' })
  if (await page.locator('.audiobook-poster-container').count() !== 1) {
    throw new Error('Expected exactly one book in the Broken books group after repair.')
  }
  if (await testId(page, 'broken-reasons').count() !== 1) {
    throw new Error('The remaining broken book did not display its reason.')
  }
  await recordFrames(page, 2800)

  ffmpeg.stdin.end()
  const [code] = await ffmpegClosed
  if (code !== 0) throw new Error(`ffmpeg exited with code ${code}`)
  recordingFinished = true
  console.log(`Captured ${process.env.OUTPUT} from ${BASE}`)
} finally {
  if (!recordingFinished) {
    ffmpeg.stdin.destroy()
    ffmpeg.kill()
  }
  if (browser) {
    await browser.close()
  }
  await restore('seeded')
}