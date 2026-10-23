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

createServer(async (req, res) => {
  const url = new URL(req.url, 'http://localhost')
  const json = (body, status = 200) => { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(body)) }
  if (url.pathname.startsWith('/hubs')) { res.writeHead(404).end(); return }
  if (url.pathname.startsWith('/api')) {
    const path = url.pathname.replace(/^\/api\/v1/, '')
    if (path === '/auth/csrf') return json({ token: 'csrf' })
    if (path === '/auth/me') return json(me)
    if (path.startsWith('/reports/sales')) return json(report)
    // The commission tariffs tab reads a list here; the generic empty answer is a page object and would break it.
    if (path.startsWith('/connections/') && path.endsWith('/commission-tariffs')) return json([])
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
