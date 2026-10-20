// A tiny static server for the built panel (dist/): files by path, index.html for every other route (the SPA fallback).
// The API is never reached through it: the capture script answers /api and /hubs requests inside the browser.
import { createServer } from 'node:http'
import { readFile, stat } from 'node:fs/promises'
import { extname, join, normalize } from 'node:path'

const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.png': 'image/png', '.webp': 'image/webp', '.jpg': 'image/jpeg', '.ico': 'image/x-icon', '.json': 'application/json', '.woff2': 'font/woff2', '.woff': 'font/woff', '.txt': 'text/plain' }

export async function startStaticServer(root) {
  const server = createServer(async (request, response) => {
    const pathname = decodeURIComponent(new URL(request.url ?? '/', 'http://localhost').pathname)
    let file = normalize(join(root, pathname))
    if (!file.startsWith(normalize(root))) { response.writeHead(403).end(); return }
    try {
      if (!(await stat(file)).isFile()) throw new Error('not a file')
    } catch {
      file = join(root, 'index.html')
    }
    const body = await readFile(file)
    response.writeHead(200, { 'Content-Type': types[extname(file)] ?? 'application/octet-stream', 'Cache-Control': 'no-store' })
    response.end(body)
  })
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  const { port } = server.address()
  return { origin: `http://127.0.0.1:${port}`, close: () => new Promise(resolve => server.close(resolve)) }
}
