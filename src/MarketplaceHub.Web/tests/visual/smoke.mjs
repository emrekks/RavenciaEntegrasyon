// Opens every main route against the mocked API (fixtures) and fails when a page shows the error boundary, throws, logs a
// console error or lands on the sign-in screen: a fixture whose shape a page no longer accepts is caught here.
//   node tests/visual/smoke.mjs [--dist dist] [--only reports,jobs] [--widths 1280,375]   (npm run test:smoke builds first)
import { resolve } from 'node:path'
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
const only = args.only ? new Set(args.only.split(',')) : null
const widths = (args.widths ?? '1280,375').split(',').map(Number)


const browser = await chromium.launch({ executablePath: await chromiumExecutable() })
const server = await startStaticServer(resolve(root, args.dist ?? 'dist'))
const failures = []
const queue = routes.filter(([name]) => !only || only.has(name)).flatMap(route => widths.map(width => [route, width]))
async function worker() {
  for (let job = queue.shift(); job; job = queue.shift()) {
    const [[name, path], width] = job
    const context = await browser.newContext({ viewport: { width, height: width < 768 ? 812 : 800 }, locale: 'tr-TR', timezoneId: 'Europe/Istanbul', reducedMotion: 'reduce' })
    await context.route(url => !url.href.startsWith(server.origin), route => route.abort())
    const page = await context.newPage()
    await page.clock.setFixedTime(frozenNow)
    const unhandled = new Set()
    await installApiMock(page, { unhandled })
    const problems = []
    page.on('pageerror', error => problems.push(`page error: ${error.message}`))
    page.on('console', message => { if (message.type() === 'error' && !/Failed to load resource|net::ERR|negotiation|Failed to start the connection/.test(message.text())) problems.push(`console: ${message.text().split('\n')[0]}`) })
    try {
      await page.goto(server.origin + path)
      await page.locator('.app-shell main').first().waitFor({ timeout: 15000 })
      await page.waitForLoadState('networkidle').catch(() => undefined)
      await page.waitForTimeout(600)
      if (await page.locator('.rv-state.is-error, [role="alert"].rv-state').count()) problems.push(`error screen: ${(await page.locator('.rv-state.is-error').first().innerText()).replace(/\s+/g, ' ').slice(0, 160)}`)
      if (!(await page.locator('.app-shell main').first().innerText()).trim()) problems.push('empty page')
    } catch (error) { problems.push(`did not render: ${error.message.split('\n')[0]}`) }
    for (const request of unhandled) if (request.startsWith('GET ')) problems.push(`no fixture: ${request}`)
    if (problems.length) failures.push(`${name}@${width} (${path}): ${[...new Set(problems)].join(' | ')}`)
    await context.close()
  }
}
try { await Promise.all(Array.from({ length: 4 }, worker)) } finally { await browser.close(); await server.close() }
console.log(failures.length ? `Smoke failures:\n  ${failures.join('\n  ')}` : `smoke ok: ${routes.length} routes x ${widths.length} widths`)
process.exitCode = failures.length ? 1 : 0
