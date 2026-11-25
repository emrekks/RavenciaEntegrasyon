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
// A phone's bottom bar is hidden while a layer is open (a filter sheet, a window) and comes back when it closes.
async function layerStep(name, path, open, close) {
  const context = await browser.newContext({ viewport: { width: 375, height: 812 }, locale: 'tr-TR', timezoneId: 'Europe/Istanbul', reducedMotion: 'reduce', hasTouch: true, isMobile: true })
  await context.route(url => !url.href.startsWith(server.origin), route => route.abort())
  const page = await context.newPage()
  await page.clock.setFixedTime(frozenNow)
  await installApiMock(page, { unhandled: new Set() })
  try {
    await page.goto(server.origin + path)
    await page.locator('.app-shell main').first().waitFor({ timeout: 15000 })
    await page.waitForLoadState('networkidle').catch(() => undefined)
    await page.waitForTimeout(600)
    const dockHidden = () => page.evaluate(() => { const layer = document.querySelector('.rv-dock-layer'); const style = layer && getComputedStyle(layer); return Boolean(layer && layer.hasAttribute('inert') && style.visibility === 'hidden') })
    if (await dockHidden()) failures.push(`layer step ${name}: the bar is hidden before the layer opens`)
    await open(page)
    await page.waitForTimeout(500)
    if (!(await dockHidden())) failures.push(`layer step ${name}: the bar stays visible while the layer is open`)
    await close(page)
    await page.waitForTimeout(500)
    if (await dockHidden()) failures.push(`layer step ${name}: the bar does not come back when the layer closes`)
  } catch (error) { failures.push(`layer step ${name}: ${String(error.message).split(/\r?\n/)[0]}`) }
  await context.close()
}
// The dock's counts stay inside the capsule (5 px from the capsule's top and right edge at least, ring included, 3 px from the others) with one, two and three characters.
async function badgeStep() {
  const context = await browser.newContext({ viewport: { width: 375, height: 812 }, locale: 'tr-TR', timezoneId: 'Europe/Istanbul', reducedMotion: 'reduce', hasTouch: true, isMobile: true })
  await context.route(url => !url.href.startsWith(server.origin), route => route.abort())
  const page = await context.newPage()
  await page.clock.setFixedTime(frozenNow)
  await installApiMock(page, { unhandled: new Set() })
  try {
    await page.goto(server.origin + '/dashboard')
    await page.locator('.rv-dock').waitFor({ timeout: 15000 })
    for (const text of ['5', '12', '99+']) {
      const bad = await page.evaluate(label => {
        const parts = [...document.querySelectorAll('.rv-dock-slot')]
        const problems = []
        for (const part of parts) {
          let badge = part.querySelector('.rv-dock-badge')
          if (!badge) { badge = document.createElement('span'); badge.className = 'rv-dock-badge'; part.querySelector('.rv-dock-icon').append(badge) }
          badge.textContent = label
          const box = part.closest('.rv-dock').getBoundingClientRect()
          const rect = badge.getBoundingClientRect()
          if (rect.top < box.top + 6.5 || rect.right > box.right - 6.5 || rect.left < box.left + 3 || rect.bottom > box.bottom - 3) problems.push((part.getAttribute('aria-label') ?? '?') + ' ' + label)
        }
        return problems
      }, text)
      for (const item of bad) failures.push('dock count out of its part: ' + item)
    }
  } catch (error) { failures.push('dock count step: ' + String(error.message).split(/\r?\n/)[0]) }
  await context.close()
}
// The dock's inner pill (the glide) in every slot: it keeps 4 px at least from the capsule's inner edge on every side (6 px by design),
// is never wider than its slot, and is concentric with the capsule's round ends in the first and the last slot.
async function glideStep() {
  const context = await browser.newContext({ viewport: { width: 360, height: 780 }, locale: 'tr-TR', timezoneId: 'Europe/Istanbul', reducedMotion: 'reduce', hasTouch: true, isMobile: true })
  await context.route(url => !url.href.startsWith(server.origin), route => route.abort())
  const page = await context.newPage()
  await page.clock.setFixedTime(frozenNow)
  await installApiMock(page, { unhandled: new Set() })
  try {
    await page.goto(server.origin + '/dashboard')
    await page.locator('.rv-dock').waitFor({ timeout: 15000 })
    const measure = () => page.evaluate(() => {
      const dockEl = document.querySelector('.rv-dock'); const slotsEl = [...dockEl.querySelectorAll('.rv-dock-slot')]; const early = []
      const box = dockEl.getBoundingClientRect()
      if (Math.abs(box.height - 62) > 0.5) early.push('capsule height ' + box.height.toFixed(1))
      const iconY = slotsEl.map(slotEl => { const r = slotEl.querySelector('.ui-icon').getBoundingClientRect(); return r.top + r.height / 2 })
      const labelY = slotsEl.map(slotEl => slotEl.querySelector('.rv-dock-label').getBoundingClientRect().bottom)
      if (Math.max(...iconY) - Math.min(...iconY) > 0.5) early.push('icons not on one line ' + iconY.map(v => v.toFixed(1)).join('/'))
      if (Math.max(...labelY) - Math.min(...labelY) > 0.5) early.push('labels not on one baseline ' + labelY.map(v => v.toFixed(1)).join('/'))
      slotsEl.forEach((slotEl, i) => {
        const icon = slotEl.querySelector('.ui-icon').getBoundingClientRect(); const text = slotEl.querySelector('.rv-dock-label').getBoundingClientRect(); const slotBox = slotEl.getBoundingClientRect()
        if (Math.abs(icon.width - 22) > 0.5 || Math.abs(icon.height - 22) > 0.5) early.push(i + ' icon size ' + icon.width.toFixed(1))
        if (text.height < 11 || text.width > slotBox.width - 4) early.push(i + ' label cut ' + text.width.toFixed(1) + '/' + slotBox.width.toFixed(1) + '/' + text.height.toFixed(1))
      })
      return early
    })
      const bad = await page.evaluate(() => {
        const dockEl = document.querySelector('.rv-dock'); const slotsEl = [...dockEl.querySelectorAll('.rv-dock-slot')]; const early = []
        for (const label of ['5', '26', '99+']) {
          slotsEl.forEach((slotEl, i) => {
            let badge = slotEl.querySelector('.rv-dock-badge'); if (!badge) { badge = document.createElement('span'); badge.className = 'rv-dock-badge'; slotEl.querySelector('.rv-dock-icon').append(badge) } badge.textContent = label
            const box = dockEl.getBoundingClientRect(); const slotBox = slotEl.getBoundingClientRect(); const icon = slotEl.querySelector('.ui-icon').getBoundingClientRect(); const b = badge.getBoundingClientRect()
            const mode = i + ' ' + label
            if (Math.abs(icon.left + icon.width / 2 - (slotBox.left + slotBox.width / 2)) > 0.5) early.push(mode + ' icon not centred in x ' + (icon.left + icon.width / 2 - (slotBox.left + slotBox.width / 2)).toFixed(2))
            if (b.top - box.top - 1.5 < 5 || box.right - b.right - 1.5 < 5) early.push(mode + ' count too near the capsule edge ' + (b.top - box.top - 1.5).toFixed(1) + '/' + (box.right - b.right - 1.5).toFixed(1))
          })
        }
        const dock = document.querySelector('.rv-dock'); const glide = dock.querySelector('.rv-dock-glide'); const problems = [...early]
        for (let index = 0; index < 5; index += 1) {
          dock.style.setProperty('--rv-dock-i', String(index))
          const box = dock.getBoundingClientRect(); const pill = glide.getBoundingClientRect(); const slot = dock.querySelectorAll('.rv-dock-slot')[index].getBoundingClientRect()
          const gaps = [pill.left - box.left, box.right - pill.right, pill.top - box.top, box.bottom - pill.bottom]
          if (Math.min(...gaps) < 4) problems.push(index + ' gap ' + gaps.map(g => g.toFixed(1)).join('/'))
          if (pill.width > slot.width - 3.5 || pill.left < slot.left + 1.5 || pill.right > slot.right - 1.5) problems.push(index + ' pill outside its slot ' + [pill.left - slot.left, slot.right - pill.right, pill.width, slot.width].map(v => v.toFixed(2)).join('/'))
          if ((index === 0 || index === 4) && Math.abs((index === 0 ? gaps[0] : gaps[1]) - gaps[2]) > 0.5) problems.push(index + ' not concentric')
        }
        return problems
      })
      for (const item of bad) failures.push('dock pill: ' + item)
      for (const item of await measure()) failures.push('dock still: ' + item)
      // Scrolled down: the capsule, its icons and its labels are exactly the same (the dock has one size).
      for (let step = 0; step < 14; step += 1) { await page.mouse.wheel(0, 120); await page.waitForTimeout(40) }
      await page.waitForTimeout(500)
      for (const item of await measure()) failures.push('dock scrolled: ' + item)
  } catch (error) { failures.push('dock pill step: ' + String(error.message).split(String.fromCharCode(10))[0]) }
  await context.close()
}
try {
  await Promise.all(Array.from({ length: 4 }, worker))
  if (!only) await badgeStep()
  if (!only) await glideStep()
  if (!only) {
    await layerStep('filter sheet', '/orders', page => page.getByRole('button', { name: /^Filtre/ }).first().click(), page => page.getByRole('button', { name: /^Filtrele$/ }).first().click())
    await layerStep('window', '/orders', page => page.getByRole('button', { name: /Detayları Aç/ }).first().click(), page => page.keyboard.press('Escape'))
  }
} finally { await browser.close(); await server.close() }
console.log(failures.length ? `Smoke failures:\n  ${failures.join('\n  ')}` : `smoke ok: ${routes.length} routes x ${widths.length} widths`)
process.exitCode = failures.length ? 1 : 0
