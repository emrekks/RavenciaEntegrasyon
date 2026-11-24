// Screenshots of the main screens against the mocked API, for looking at a change (or a width) by eye.
//   node tests/visual/shots.mjs --out <folder> [--prefix tablet] [--only orders,reports] [--widths 820,1024] [--height 1000] [--themes dark,light] [--full]
// Files: <folder>/<prefix>-<screen>-<width>-<theme>.png. Needs a build in dist/ (npm run build:assets or vite build).
import { mkdir } from 'node:fs/promises'
import { join, resolve } from 'node:path'
import { chromium } from '@playwright/test'
import { chromiumExecutable } from './browser.mjs'
import { startStaticServer } from './server.mjs'
import { installApiMock } from './api-mock.mjs'
import { frozenNow } from './scenarios.mjs'
import { routes } from './routes.mjs'

const args = Object.fromEntries(process.argv.slice(2).reduce((pairs, value, index, all) => {
  if (value.startsWith('--')) pairs.push([value.slice(2), all[index + 1] && !all[index + 1].startsWith('--') ? all[index + 1] : 'true'])
  return pairs
}, []))
const root = resolve(import.meta.dirname, '../..')
const out = resolve(args.out ?? join(root, 'tests/visual/.out/shots'))
const only = args.only ? new Set(args.only.split(',')) : null
const widths = (args.widths ?? '1280').split(',').map(Number)
const themes = (args.themes ?? 'dark').split(',')
const prefix = args.prefix ?? 'ekran'
await mkdir(out, { recursive: true })
const browser = await chromium.launch({ executablePath: await chromiumExecutable() })
const server = await startStaticServer(resolve(root, args.dist ?? 'dist'))
const queue = routes.filter(([name]) => !only || only.has(name)).flatMap(route => widths.flatMap(width => themes.map(theme => [route, width, theme])))
async function worker() {
  for (let job = queue.shift(); job; job = queue.shift()) {
    const [[name, path], width, theme] = job
    const context = await browser.newContext({ viewport: { width, height: Number(args.height ?? 900) }, locale: 'tr-TR', timezoneId: 'Europe/Istanbul', reducedMotion: 'reduce' })
    await context.addInitScript(value => { try { localStorage.setItem('ravencia.visualTheme', value) } catch { /* no storage */ } }, theme)
    await context.route(url => !url.href.startsWith(server.origin), route => route.abort())
    const page = await context.newPage()
    await page.clock.setFixedTime(frozenNow)
    await installApiMock(page)
    try {
      await page.goto(server.origin + path)
      await page.locator('.app-shell main').first().waitFor({ timeout: 15000 })
      await page.waitForLoadState('networkidle').catch(() => undefined)
      await page.waitForTimeout(900)
      await page.screenshot({ path: join(out, `${prefix}-${name}-${width}-${theme}.png`), fullPage: args.full === 'true' })
      console.log(`shot ${name} ${width} ${theme}`)
    } catch (error) { console.log(`FAILED ${name} ${width} ${theme}: ${error.message.split('\n')[0]}`) }
    await context.close()
  }
}
try { await Promise.all(Array.from({ length: 4 }, worker)) } finally { await browser.close(); await server.close() }
