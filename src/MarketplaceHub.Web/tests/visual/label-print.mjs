// Prints a shipping label from the Siparişler page of the built panel (dist/) the way Chrome prints it: print media,
// the page size the label's own CSS asks for, and the page scrolled so an open window has pinned the body
// (overlay-scroll-lock.ts). Checks that the PDF is one page of the label's size and that the label is on it.
// A 100 x 100 mm sticker and an A4 page with one label are both printed.
//
//   npx vite build && node tests/visual/label-print.mjs [--dist dist] [--out tests/visual/.out/label-print]
import { mkdir, readFile } from 'node:fs/promises'
import { join, resolve } from 'node:path'
import { chromium } from '@playwright/test'
import { chromiumExecutable } from './browser.mjs'
import { startStaticServer } from './server.mjs'
import { installApiMock } from './api-mock.mjs'

const argument = (name, fallback) => { const at = process.argv.indexOf(`--${name}`); return at > 0 ? process.argv[at + 1] : fallback }
const web = resolve(import.meta.dirname, '../..')
const dist = resolve(web, argument('dist', 'dist'))
const out = resolve(web, argument('out', 'tests/visual/.out/label-print'))

// Page count and page sizes in mm, read from the PDF itself, and whether its pages use a font (a page without any text
// on it has none).
export async function pdfSummary(path) {
  const text = (await readFile(path)).toString('latin1')
  const pages = (text.match(/\/Type\s*\/Page(?!s)/g) ?? []).length
  const sizes = [...text.matchAll(/\/MediaBox\s*\[\s*([\d.\s-]+)\]/g)].slice(0, pages).map(match => {
    const [x0, y0, x1, y1] = match[1].trim().split(/\s+/).map(Number)
    return [Math.round((x1 - x0) / 72 * 25.4), Math.round((y1 - y0) / 72 * 25.4)]
  })
  return { pages, sizes, fonts: (text.match(/\/Type\s*\/Font\b/g) ?? []).length }
}

// The first printed page as the printer lays it out: the window is the page (print media, the page's size in CSS
// pixels, scrolled to the top), so a page-sized fixed body clips the same way. Returns the share of dark pixels in it.
async function firstPageInk(page, sizeMm) {
  const px = mm => Math.round(mm * 96 / 25.4)
  await page.setViewportSize({ width: px(sizeMm[0]), height: px(sizeMm[1]) })
  await page.evaluate(() => window.scrollTo(0, 0))
  const shot = await page.screenshot()
  return page.evaluate(async source => {
    const image = new Image()
    image.src = `data:image/png;base64,${source}`
    await image.decode()
    const canvas = new OffscreenCanvas(image.width, image.height)
    const context = canvas.getContext('2d')
    context.drawImage(image, 0, 0)
    const data = context.getImageData(0, 0, image.width, image.height).data
    let dark = 0
    for (let index = 0; index < data.length; index += 4) if (data[index] + data[index + 1] + data[index + 2] < 200) dark++
    return dark / (data.length / 4)
  }, shot.toString('base64'))
}

const settingsFor = format => ({ settings: { senderName: 'Ravencia', senderAddress: '', defaultFormat: format, showA4Button: true, showStickerButton: true, a4LabelsPerPage: 1, stickerWidthMm: 100, stickerHeightMm: 100, showCustomerPhone: true, sectionGapMm: 4 }, version: 1 })

const cases = [
  { name: 'sticker', format: 'sticker', size: [100, 100] },
  { name: 'a4', format: 'a4', size: [210, 297] }
]

await mkdir(out, { recursive: true })
const server = await startStaticServer(dist)
const browser = await chromium.launch({ executablePath: await chromiumExecutable() })
const failures = []
try {
  for (const item of cases) {
    const page = await browser.newPage({ viewport: { width: 1280, height: 800 } })
    await installApiMock(page, { overrides: [['GET /settings/shipping-label', settingsFor(item.format)]] })
    await page.goto(`${server.origin}/orders`)
    const open = page.getByRole('button', { name: 'Etiket', exact: true }).first()
    await open.waitFor()
    // An open window pins the body at the scroll position it had (position: fixed; top: -y).
    await page.evaluate(() => window.scrollTo(0, 400))
    await open.click()
    const print = page.locator('.shipping-label-modal').getByRole('button', { name: 'Yazdır' })
    await print.waitFor()
    await page.waitForFunction(() => !document.querySelector('.shipping-label-modal .rr-primary')?.hasAttribute('disabled'))
    await page.locator('.shipping-label-print-surface').waitFor()
    await page.emulateMedia({ media: 'print' })
    const placed = await page.evaluate(() => {
      const label = document.querySelector('.shipping-label-print-surface').getBoundingClientRect()
      const body = document.body.getBoundingClientRect()
      return { top: Math.round(label.top - body.top + window.scrollY), left: Math.round(label.left), width: Math.round(label.width), height: Math.round(label.height), bodyPosition: getComputedStyle(document.body).position }
    })
    if (process.env.LABEL_PRINT_DEBUG) console.log(JSON.stringify(await page.evaluate(() => {
      const list = []
      for (let element = document.querySelector('.shipping-label-print-surface'); element; element = element.parentElement) { const style = getComputedStyle(element); const rect = element.getBoundingClientRect(); list.push(`${element.tagName}.${String(element.className).slice(0, 40)} ${style.display} ${style.position} pad=${style.padding} mar=${style.margin} rect=${Math.round(rect.left)},${Math.round(rect.top)} ${Math.round(rect.width)}x${Math.round(rect.height)}`) }
      const before = Array.from(document.querySelectorAll('.shipping-label-backdrop *')).filter(element => element.getBoundingClientRect().height > 0 && !element.closest('.shipping-label-print-surface')).map(element => `${element.tagName}.${String(element.className).slice(0, 30)} ${Math.round(element.getBoundingClientRect().top)}+${Math.round(element.getBoundingClientRect().height)}`)
      const label = document.querySelector('.shipping-label-print-surface').getBoundingClientRect()
      const top = document.elementFromPoint(label.left + 30, label.top + label.height / 2)
      const layers = []
      for (let element = top; element; element = element.parentElement) { const style = getComputedStyle(element); const pseudo = ['::before', '::after'].map(name => { const p = getComputedStyle(element, name); return p.content !== 'none' && p.content !== 'normal' ? `${name}:${p.backgroundColor}/${p.position}/${p.opacity}` : '' }).join(''); layers.push(`${element.tagName}.${String(element.className).slice(0, 25)} bg=${style.backgroundColor} op=${style.opacity} filter=${style.filter} mix=${style.mixBlendMode} ${pseudo}`) }
      return { layers, list, before: before.slice(0, 30), bodyKids: Array.from(document.body.children).map(child => `${child.tagName}.${String(child.className).slice(0, 30)} ${getComputedStyle(child).display}`) }
    })))
    const path = join(out, `${item.name}.pdf`)
    await page.pdf({ path, preferCSSPageSize: true, printBackground: true })
    const pdf = await pdfSummary(path)
    const ink = await firstPageInk(page, item.size)
    await page.screenshot({ path: join(out, `${item.name}-page1.png`) })
    const result = { case: item.name, ...pdf, ink: Math.round(ink * 1000) / 10, placed }
    console.log(JSON.stringify(result))
    const problems = []
    if (pdf.pages !== 1) problems.push(`${pdf.pages} pages, expected 1`)
    if (pdf.sizes[0]?.join('x') !== item.size.join('x')) problems.push(`page ${pdf.sizes[0]?.join(' x ')} mm, expected ${item.size.join(' x ')} mm`)
    // The label's barcode bars and bold text cover a good part of the page; a blank page has next to none.
    if (ink < (item.format === 'sticker' ? 0.05 : 0.01) || !pdf.fonts) problems.push(`the first page is blank or nearly so (${(ink * 100).toFixed(1)} % ink, ${pdf.fonts} fonts)`)
    if (placed.bodyPosition === 'fixed') problems.push('the body stays pinned (position: fixed) in print')
    if (placed.top > 40) problems.push(`the label starts ${placed.top} px down the printed page, not at its top`)
    if (problems.length) failures.push(`${item.name}: ${problems.join('; ')}`)
    await page.close()
  }
} finally {
  await browser.close()
  await server.close()
}
if (failures.length) { console.error(failures.join('\n')); process.exitCode = 1 } else console.log('label print: every case is one page of the label, with the label on it')
