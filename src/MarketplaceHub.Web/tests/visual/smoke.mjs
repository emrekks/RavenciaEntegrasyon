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
// The dock's counts stay inside the capsule (5 px from the capsule's top and right edge by design, ring included: 4.5 is checked, so sub-pixel rounding cannot flip it; 3 px from the others) with one, two and three characters.
async function badgeStep() {
  const context = await browser.newContext({ viewport: { width: 375, height: 812 }, locale: 'tr-TR', timezoneId: 'Europe/Istanbul', reducedMotion: 'reduce', hasTouch: true, isMobile: true })
  await context.route(url => !url.href.startsWith(server.origin), route => route.abort())
  const page = await context.newPage()
  await page.clock.setFixedTime(frozenNow)
  await installApiMock(page, { unhandled: new Set() })
  try {
    await page.goto(server.origin + '/dashboard')
    await page.locator('.rv-dock').waitFor({ timeout: 15000 })
    await page.evaluate(async () => { await document.fonts.ready; await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))) })
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
          if (rect.top < box.top + 6 || rect.right > box.right - 6 || rect.left < box.left + 3 || rect.bottom > box.bottom - 3) problems.push((part.getAttribute('aria-label') ?? '?') + ' ' + label)
        }
        return problems
      }, text)
      for (const item of bad) failures.push('dock count out of its part: ' + item)
    }
  } catch (error) { failures.push('dock count step: ' + String(error.message).split(/\r?\n/)[0]) }
  await context.close()
}
// The dock's inner pill (the glide) in every slot: it keeps 4 px from the capsule's inner edge on every side (4.5 px by design; 3.5 is checked),
// is never wider than its slot, leaves its label 8 px at both sides by design (7 is checked: the label is 49.2 px in a 65.4 px pill at 360 px, and sub-pixel or font rounding must not flip it), and is concentric with the capsule's round ends in the first and the last slot.
async function glideStep() {
  const context = await browser.newContext({ viewport: { width: 360, height: 780 }, locale: 'tr-TR', timezoneId: 'Europe/Istanbul', reducedMotion: 'reduce', hasTouch: true, isMobile: true })
  await context.route(url => !url.href.startsWith(server.origin), route => route.abort())
  const page = await context.newPage()
  await page.clock.setFixedTime(frozenNow)
  await installApiMock(page, { unhandled: new Set() })
  try {
    await page.goto(server.origin + '/dashboard')
    await page.locator('.rv-dock').waitFor({ timeout: 15000 })
    await page.evaluate(async () => { await document.fonts.ready; await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))) })
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
        for (const label of ['5', '14', '99+']) {
          const offsets = []
          slotsEl.forEach((slotEl, i) => {
            let badge = slotEl.querySelector('.rv-dock-badge'); if (!badge) { badge = document.createElement('span'); badge.className = 'rv-dock-badge'; slotEl.querySelector('.rv-dock-icon').append(badge) } badge.textContent = label
            const box = dockEl.getBoundingClientRect(); const slotBox = slotEl.getBoundingClientRect(); const icon = slotEl.querySelector('.ui-icon').getBoundingClientRect(); const b = badge.getBoundingClientRect()
            const mode = i + ' ' + label
            offsets.push([b.left - icon.right, b.top - icon.top])
            if (Math.abs(icon.left + icon.width / 2 - (slotBox.left + slotBox.width / 2)) > 0.5) early.push(mode + ' icon not centred in x ' + (icon.left + icon.width / 2 - (slotBox.left + slotBox.width / 2)).toFixed(2))
            if (b.top - box.top - 1.5 < 4.5 || box.right - b.right - 1.5 < 4.5) early.push(mode + ' count too near the capsule edge ' + (b.top - box.top - 1.5).toFixed(1) + '/' + (box.right - b.right - 1.5).toFixed(1))
          })
          for (const axis of [0, 1]) { const values = offsets.map(o => o[axis]); if (Math.max(...values) - Math.min(...values) > 0.5) early.push(label + ' count offset differs between the slots (' + (axis ? 'y' : 'x') + ') ' + values.map(v => v.toFixed(1)).join('/')) }
        }
        const dock = document.querySelector('.rv-dock'); const glide = dock.querySelector('.rv-dock-glide'); const problems = [...early]
        for (let index = 0; index < 5; index += 1) {
          dock.style.setProperty('--rv-dock-i', String(index))
          const box = dock.getBoundingClientRect(); const pill = glide.getBoundingClientRect(); const slot = dock.querySelectorAll('.rv-dock-slot')[index].getBoundingClientRect()
          const gaps = [pill.left - box.left, box.right - pill.right, pill.top - box.top, box.bottom - pill.bottom]
          if (Math.min(...gaps) < 3.5) problems.push(index + ' gap ' + gaps.map(g => g.toFixed(1)).join('/'))
          if (pill.width > slot.width || pill.left < slot.left - 0.01 || pill.right > slot.right + 0.01) problems.push(index + ' pill outside its slot ' + [pill.left - slot.left, slot.right - pill.right, pill.width, slot.width].map(v => v.toFixed(2)).join('/'))
          const labelBox = dock.querySelectorAll('.rv-dock-slot')[index].querySelector('.rv-dock-label').getBoundingClientRect()
          const room = Math.min(labelBox.left - pill.left, pill.right - labelBox.right)
          if (room < 7) problems.push(index + ' label too close to its pill edge ' + room.toFixed(1))
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
// A question's detail on a phone: nothing inside the body runs past its content edge (a nowrap line used to widen the grid column and cut the bubbles, times and note), and the body itself does not scroll sideways.
async function questionDetailStep() {
  for (const width of [320, 360, 375, 393, 430]) {
    const context = await browser.newContext({ viewport: { width, height: 812 }, locale: 'tr-TR', timezoneId: 'Europe/Istanbul', reducedMotion: 'reduce', hasTouch: true, isMobile: true })
    await context.route(url => !url.href.startsWith(server.origin), route => route.abort())
    const page = await context.newPage()
    await page.clock.setFixedTime(frozenNow)
    await installApiMock(page, { unhandled: new Set() })
    try {
      await page.goto(server.origin + '/questions')
      await page.getByText('Merhaba, tuniği', { exact: false }).first().click()
      await page.locator('.qi-detail-body .qi-history-item').first().waitFor({ timeout: 15000 })
      await page.evaluate(async () => { await document.fonts.ready; await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))) })
      const bad = await page.evaluate(() => {
        const body = document.querySelector('.qi-detail-body'); const style = getComputedStyle(body); const box = body.getBoundingClientRect()
        const edge = box.right - parseFloat(style.paddingRight) - parseFloat(style.borderRightWidth)
        const problems = []
        if (body.scrollWidth > body.clientWidth) problems.push('body scrolls sideways ' + body.scrollWidth + '/' + body.clientWidth)
        for (const el of body.querySelectorAll('*')) {
          const rect = el.getBoundingClientRect()
          if (rect.width && rect.right > edge + 0.5) problems.push((el.className && String(el.className).split(' ')[0] || el.tagName.toLowerCase()) + ' +' + (rect.right - edge).toFixed(1) + 'px')
        }
        return problems
      })
      if (bad.length) failures.push('question detail @' + width + ': ' + [...new Set(bad)].slice(0, 8).join(', '))
    } catch (error) { failures.push('question detail @' + width + ': ' + String(error.message).split(String.fromCharCode(10))[0]) }
    await context.close()
  }
}
// The question page's store strip on a phone: long store names are cut with an ellipsis inside their own cell and the record
// count stays whole beside them (a nowrap name used to push the count into the next column).
async function questionSyncStripStep() {
  const longStates = [
    { connectionId: 'c-ty', platformCode: 'TRENDYOL', storeName: 'Ravencia Moda Trendyol Premium Mağaza Uzun Ad', historyImported: true, progressStatus: 'COMPLETED', importedCount: 1284, productQuestionCount: 1284, orderQuestionCount: 0, lastSuccessAt: '2026-10-20T06:21:00Z', lastError: null },
    { connectionId: 'c-hb', platformCode: 'HEPSIBURADA', storeName: 'Ravencia Moda Hepsiburada Resmi Satıcı Mağazası', historyImported: true, progressStatus: 'COMPLETED', importedCount: 642, productQuestionCount: 518, orderQuestionCount: 124, lastSuccessAt: '2026-10-20T06:19:00Z', lastError: null },
  ]
  for (const width of [320, 360, 375, 393, 430]) {
    const context = await browser.newContext({ viewport: { width, height: 812 }, locale: 'tr-TR', timezoneId: 'Europe/Istanbul', reducedMotion: 'reduce', hasTouch: true, isMobile: true })
    await context.route(url => !url.href.startsWith(server.origin), route => route.abort())
    const page = await context.newPage()
    await page.clock.setFixedTime(frozenNow)
    await installApiMock(page, { unhandled: new Set(), overrides: [['GET /questions/sync-state', longStates]] })
    try {
      await page.goto(server.origin + '/questions')
      await page.locator('.qi-sync .rv-questions-sync-item').first().waitFor({ timeout: 15000 })
      await page.evaluate(async () => { await document.fonts.ready; await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))) })
      const bad = await page.evaluate(() => {
        const problems = []
        for (const cell of document.querySelectorAll('.qi-sync .rv-questions-sync-item')) {
          const box = cell.getBoundingClientRect()
          for (const el of cell.querySelectorAll('*')) {
            const rect = el.getBoundingClientRect()
            if (rect.width && getComputedStyle(el).display !== 'none' && rect.right > box.right + 0.5) problems.push((el.className && String(el.className).split(' ')[0] || el.tagName.toLowerCase()) + ' +' + (rect.right - box.right).toFixed(1) + 'px')
          }
        }
        return problems
      })
      if (bad.length) failures.push('question store strip @' + width + ': ' + [...new Set(bad)].slice(0, 6).join(', '))
    } catch (error) { failures.push('question store strip @' + width + ': ' + String(error.message).split(String.fromCharCode(10))[0]) }
    await context.close()
  }
}
// The question page's head on the narrowest phones: the page does not scroll sideways and Hazır Cevaplar and Yenile keep
// their icon, label and count inside their own boxes, side by side.
async function questionHeadStep() {
  for (const width of [320, 360, 375]) {
    const context = await browser.newContext({ viewport: { width, height: 812 }, locale: 'tr-TR', timezoneId: 'Europe/Istanbul', reducedMotion: 'reduce', hasTouch: true, isMobile: true })
    await context.route(url => !url.href.startsWith(server.origin), route => route.abort())
    const page = await context.newPage()
    await page.clock.setFixedTime(frozenNow)
    await installApiMock(page, { unhandled: new Set() })
    try {
      await page.goto(server.origin + '/questions')
      await page.locator('.qi-acts > button').nth(1).waitFor({ timeout: 15000 })
      await page.evaluate(async () => { await document.fonts.ready; await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))) })
      const bad = await page.evaluate(() => {
        const problems = []
        if (document.documentElement.scrollWidth > innerWidth) problems.push('page scrolls sideways ' + document.documentElement.scrollWidth + '/' + innerWidth)
        const buttons = [...document.querySelectorAll('.qi-acts > button')]
        for (const button of buttons) if (button.scrollWidth > button.clientWidth) problems.push('"' + button.textContent.trim() + '" runs past its button ' + button.scrollWidth + '/' + button.clientWidth)
        const [first, second] = buttons.map(button => button.getBoundingClientRect())
        if (first && second && (Math.abs(first.top - second.top) > 1 || first.right > second.left || second.right > innerWidth)) problems.push('buttons are not side by side inside the page')
        return problems
      })
      if (bad.length) failures.push('question head @' + width + ': ' + bad.join(', '))
    } catch (error) { failures.push('question head @' + width + ': ' + String(error.message).split(String.fromCharCode(10))[0]) }
    await context.close()
  }
}
try {
  await Promise.all(Array.from({ length: 4 }, worker))
  if (!only) await badgeStep()
  if (!only) await glideStep()
  if (!only) await questionDetailStep()
  if (!only) await questionSyncStripStep()
  if (!only) await questionHeadStep()
  if (!only) {
    await layerStep('filter sheet', '/orders', page => page.getByRole('button', { name: /^Filtre/ }).first().click(), page => page.getByRole('button', { name: /^Filtrele$/ }).first().click())
    await layerStep('window', '/orders', page => page.getByRole('button', { name: /Detayları Aç/ }).first().click(), page => page.keyboard.press('Escape'))
  }
} finally { await browser.close(); await server.close() }
console.log(failures.length ? `Smoke failures:\n  ${failures.join('\n  ')}` : `smoke ok: ${routes.length} routes x ${widths.length} widths`)
process.exitCode = failures.length ? 1 : 0
