// Local preview of a built panel for the reports page: the build folder (argument 1) as files, /api answered from the
// visual-test fixtures plus a realistic 30-day sales report (report-data.mjs). No backend or database is needed.
import { createServer } from 'node:http'
import { readFile, stat } from 'node:fs/promises'
import { extname, join, normalize, resolve } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { report } from './report-data.mjs'

const root = resolve(process.argv[2] ?? '')
const port = Number(process.argv[3] ?? 4319)
if (!process.argv[2]) { console.error('usage: node serve.mjs <build folder> [port]'); process.exit(1) }
const web = fileURLToPath(new URL('../../../src/MarketplaceHub.Web/', import.meta.url))
const { fixtures } = await import(pathToFileURL(join(web, 'tests/visual/fixtures.mjs')).href)
const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.png': 'image/png', '.webp': 'image/webp', '.woff2': 'font/woff2', '.json': 'application/json' }
const me = { id: 'u1', email: 'demo@ravencia.com', displayName: 'Önizleme', role: 'OWNER', state: 'ACTIVE', tenantId: 't1' }
const empty = { items: [], totalCount: 0, hasMore: false, nextCursor: null }
// A Trendyol tariff week (22-29 Oct) and products priced in it, worked out as CommissionTariffChoices does.
const tariff = { id: 'tr1', platformCode: 'TRENDYOL', periodStart: '2026-10-22T05:00:00Z', periodEnd: '2026-10-29T05:00:00Z', periodText: '22 Ekim 08.00-29 Ekim 07.59', fileName: 'tarife.xlsx', uploadedAt: '2026-10-22T22:27:00Z', rowCount: 3, matchedRowCount: 3, isCurrent: true }
const bands = [{ tier: 1, min: 752.65, max: null, rate: 21.5 }, { tier: 2, min: 712.76, max: 752.64, rate: 20.8 }, { tier: 3, min: 656.92, max: 712.75, rate: 20 }, { tier: 4, min: null, max: 656.91, rate: 18.8 }]
const choice = (tier, price, rate, cost) => { const commission = Math.round(price * rate) / 100; return { tier, price, rate, commission, remaining: cost === null ? null : Math.round((price - commission - cost) * 100) / 100 } }
const tariffRow = (id, name, size, basis, cost) => {
  const choices = bands.map(band => choice(band.tier, band.max ?? (basis >= band.min ? basis : band.min), band.rate, cost))
  const best = cost === null ? null : [...choices].sort((a, b) => b.remaining - a.remaining)[0].tier
  const current = bands.find(band => (band.min === null || basis >= band.min) && (band.max === null || basis <= band.max))
  return { id, barcode: `MZ005S10${id}`, sellerSku: `MZ005S10${id}`, modelCode: 'MZ005', productName: name, size, basisPrice: basis, currentSalePrice: basis + 8, currentRate: current.rate, currentTier: current.tier, currentBandRate: current.rate,
    bands, nextLowerBand: bands[current.tier] ?? null, matchesPanel: true, costPrice: cost, choices, recommendedTier: best }
}
const tariffRows = [
  { ...tariffRow('L', 'Kadın Bej Büyük Beden Şerit Desenli Taşlı Yırtmaçlı Likralı Viskon Anne Penye Bluz', 'L', 791.01, 320), imageUrl: '/pack/brand/ravencia-symbol-256.webp' },
  tariffRow('3XL', 'Kadın Bej Büyük Beden Şerit Desenli Taşlı Yırtmaçlı Likralı Viskon Anne Penye Bluz', '3XL', 899, 320),
  tariffRow('M', 'Kadın Bej Büyük Beden Şerit Desenli Taşlı Yırtmaçlı Likralı Viskon Anne Penye Bluz', 'M', 720, null)
]

createServer(async (req, res) => {
  const url = new URL(req.url, 'http://localhost')
  const json = (body, status = 200) => { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(body)) }
  if (url.pathname.startsWith('/hubs')) { res.writeHead(404).end(); return }
  if (url.pathname.startsWith('/api')) {
    const path = url.pathname.replace(/^\/api\/v1/, '')
    if (path === '/auth/csrf') return json({ token: 'csrf' })
    if (path === '/auth/me') return json(me)
    if (path.startsWith('/reports/sales')) return json(report)
    // The commission tariffs tab: one current period and a few products with their bands, costs and picks.
    if (path.startsWith('/connections/') && path.endsWith('/commission-tariffs')) return json([tariff])
    if (path.startsWith('/connections/') && path.endsWith('/rows')) return json({ items: tariffRows, totalCount: tariffRows.length, page: 0, pageSize: 50 })
    if (path.startsWith('/connections/') && path.endsWith('/export')) { res.writeHead(200, { 'Content-Type': 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', 'Content-Disposition': 'attachment; filename=komisyon-tarifesi-secimler-20261022.xlsx' }); res.end('preview'); return }
    for (const [matcher, answer] of fixtures) {
      const [method, pattern] = matcher.includes(' ') ? matcher.split(' ') : ['GET', matcher]
      if (method !== req.method) continue
      const match = pattern.startsWith('^') ? path.match(new RegExp(pattern)) : path === pattern ? [path] : null
      if (!match) continue
      const body = typeof answer === 'function' ? answer({ url, path, match, body: null }) : answer
      return body === undefined ? res.writeHead(204).end() : json(body)
    }
    return req.method === 'GET' ? json(empty) : res.writeHead(204).end()
  }
  let file = normalize(join(root, decodeURIComponent(url.pathname)))
  if (!file.startsWith(root)) { res.writeHead(404).end(); return }
  try { if (!(await stat(file)).isFile()) throw 0 } catch { file = join(root, 'index.html') }
  res.writeHead(200, { 'Content-Type': types[extname(file)] ?? 'application/octet-stream', 'Cache-Control': 'no-store' })
  res.end(await readFile(file))
}).listen(port, '127.0.0.1', () => console.log(`ready http://127.0.0.1:${port}/reports`))
