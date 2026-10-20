// Renders the built panel (dist/) in headless Chromium with a mocked API and a signed-in session, and records, for
// every scenario and width, the full computed style and box of every element (and of ::before/::after when they draw)
// plus a screenshot. diff.mjs compares two such captures.
//
//   node tests/visual/capture.mjs --out tests/visual/.out/current [--dist dist] [--only orders,returns] [--widths 375,1280] [--workers 4]
import { mkdir, writeFile } from 'node:fs/promises'
import { join, resolve } from 'node:path'
import { chromium } from '@playwright/test'
import { chromiumExecutable } from './browser.mjs'
import { startStaticServer } from './server.mjs'
import { installApiMock } from './api-mock.mjs'
import { scenarios, widths as defaultWidths, frozenNow } from './scenarios.mjs'

const args = Object.fromEntries(process.argv.slice(2).reduce((pairs, value, index, all) => {
  if (value.startsWith('--')) pairs.push([value.slice(2), all[index + 1] && !all[index + 1].startsWith('--') ? all[index + 1] : 'true'])
  return pairs
}, []))
const root = resolve(import.meta.dirname, '../..')
const out = resolve(root, args.out ?? 'tests/visual/.out/current')
const only = args.only ? new Set(args.only.split(',')) : null
const widths = args.widths ? args.widths.split(',').map(Number) : defaultWidths
const workers = Number(args.workers ?? 4)
const heights = { 375: 812, 768: 1024, 1280: 800, 1920: 1080 }

// Runs in the page: every element in document order with its computed style (interned) and its box.
function snapshot() {
  const values = new Map()
  const valueList = []
  const vectors = new Map()
  const vectorList = []
  const intern = value => {
    let id = values.get(value)
    if (id === undefined) { id = valueList.length; values.set(value, id); valueList.push(value) }
    return id
  }
  const probe = getComputedStyle(document.documentElement)
  const properties = Array.from({ length: probe.length }, (_, index) => probe.item(index)).filter(name => !name.startsWith('--')).sort()
  const styleId = style => {
    const vector = properties.map(name => intern(style.getPropertyValue(name))).join(',')
    let id = vectors.get(vector)
    if (id === undefined) { id = vectorList.length; vectors.set(vector, id); vectorList.push(vector) }
    return id
  }
  const elements = []
  const walk = (element, path) => {
    const tag = element.tagName.toLowerCase()
    if (['head', 'script', 'style', 'link', 'meta', 'title', 'noscript'].includes(tag)) return
    const rect = element.getBoundingClientRect()
    const before = getComputedStyle(element, '::before')
    const after = getComputedStyle(element, '::after')
    const draws = style => style.content !== 'none' && style.content !== 'normal'
    const className = typeof element.className === 'string' ? element.className : element.getAttribute('class') ?? ''
    elements.push([path, className, styleId(getComputedStyle(element)), [rect.x, rect.y, rect.width, rect.height].map(value => Math.round(value * 10) / 10).join(','), draws(before) ? styleId(before) : -1, draws(after) ? styleId(after) : -1])
    const counts = new Map()
    for (const child of element.children) {
      const childTag = child.tagName.toLowerCase()
      const index = counts.get(childTag) ?? 0
      counts.set(childTag, index + 1)
      walk(child, `${path}>${childTag}${index ? `[${index}]` : ''}`)
    }
  }
  walk(document.documentElement, 'html')
  return { properties, values: valueList, vectors: vectorList, elements, scroll: [document.documentElement.scrollWidth, document.documentElement.scrollHeight] }
}

async function settle(page) {
  await page.waitForLoadState('networkidle').catch(() => undefined)
  await page.evaluate(() => document.fonts.ready)
  let previous = ''
  for (let attempt = 0; attempt < 40; attempt += 1) {
    await page.waitForTimeout(250)
    // Lazy pictures would start loading while the full-page screenshot is taken: load them all now, then wait.
    const current = await page.evaluate(async () => {
      for (const image of document.images) if (image.loading === 'lazy') image.loading = 'eager'
      await Promise.all([...document.images].map(image => image.decode().catch(() => undefined)))
      // A scroll made by a step (a click scrolls its target into view) shows the scrollbar for a moment.
      return `${document.body.innerHTML.length}:${document.querySelectorAll('*').length}:${document.querySelectorAll('.is-scrolling, .is-scrollbar-hover').length}`
    })
    if (current === previous && current.endsWith(':0')) return
    previous = current
  }
}

const noMotion = '*,*::before,*::after{animation-duration:0s!important;animation-delay:0s!important;transition:none!important;caret-color:transparent!important}'

async function captureOne(browser, server, scenario, width, unhandled) {
  const height = heights[width] ?? 900
  const context = await browser.newContext({ viewport: { width, height }, deviceScaleFactor: 1, locale: 'tr-TR', timezoneId: 'Europe/Istanbul', colorScheme: 'light', reducedMotion: 'reduce' })
  try {
    await context.addInitScript(entries => { for (const [key, value] of entries) localStorage.setItem(key, value) }, Object.entries(scenario.storage ?? {}))
    // Nothing leaves the machine: images or logos on other hosts would make the layout depend on the network.
    await context.route(url => !url.href.startsWith(server.origin), route => route.abort())
    const page = await context.newPage()
    await page.clock.setFixedTime(frozenNow)
    await installApiMock(page, { signedIn: scenario.signedIn !== false, unhandled, overrides: scenario.api })
    const errors = []
    page.on('pageerror', error => errors.push(String(error)))
    await page.goto(server.origin + scenario.path)
    await page.addStyleTag({ content: noMotion })
    await settle(page)
    if (scenario.act) await scenario.act(page)
    // The pointer leaves the page, so no :hover style depends on where the last step clicked.
    await page.mouse.move(-10, -10)
    await settle(page)
    const name = `${scenario.name}@${width}`
    const data = await page.evaluate(snapshot)
    await writeFile(join(out, `${name}.json`), JSON.stringify({ ...data, errors }))
    await page.screenshot({ path: join(out, `${name}.png`), fullPage: true, animations: 'disabled', caret: 'hide' })
    return { name, data, errors }
  } finally {
    await context.close()
  }
}

const browser = await chromium.launch({ executablePath: await chromiumExecutable(), args: ['--font-render-hinting=none', '--disable-skia-runtime-opts', '--force-color-profile=srgb', '--disable-gpu', '--disable-partial-raster', '--disable-lcd-text'] })
const server = await startStaticServer(resolve(root, args.dist ?? 'dist'))
await mkdir(out, { recursive: true })
const unhandled = new Set()
const failures = []
const started = Date.now()
const queue = scenarios.filter(scenario => !only || only.has(scenario.name)).flatMap(scenario => (scenario.widths ?? widths).map(width => [scenario, width]))
try {
  await Promise.all(Array.from({ length: Math.max(1, workers) }, async () => {
    for (let job = queue.shift(); job; job = queue.shift()) {
      const [scenario, width] = job
      // A step that opens a window can miss on a busy machine: two more tries before the capture counts as failed.
      for (let attempt = 1; ; attempt += 1) {
        try {
          const { name, data, errors } = await captureOne(browser, server, scenario, width, unhandled)
          if (errors.length) failures.push(`${name}: ${errors.join(' | ')}`)
          console.log(`captured ${name} (${data.elements.length} elements, ${data.vectors.length} styles)`)
          break
        } catch (error) {
          if (attempt >= 3) { failures.push(`${scenario.name}@${width}: ${error.message.split('\n')[0]}`); break }
        }
      }
    }
  }))
} finally {
  await browser.close()
  await server.close()
}
await writeFile(join(out, '_meta.json'), JSON.stringify({ unhandled: [...unhandled].sort(), failures, seconds: Math.round((Date.now() - started) / 1000) }, null, 2))
if (unhandled.size) console.log(`API requests answered by the empty default:\n  ${[...unhandled].sort().join('\n  ')}`)
if (failures.length) console.log(`Failures:\n  ${failures.join('\n  ')}`)
console.log(`capture finished in ${Math.round((Date.now() - started) / 1000)} s -> ${out}`)
process.exitCode = failures.length ? 1 : 0
