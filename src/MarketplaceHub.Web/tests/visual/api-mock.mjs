// Answers the panel's API inside the browser: a signed-in owner and a fixed data set (fixtures.mjs). The realtime hub is
// refused (the panel keeps working without it). Requests no fixture covers get an empty list and are reported.
import { fixtures } from './fixtures.mjs'

const empty = { items: [], totalCount: 0, hasMore: false, nextCursor: null }
const me = { id: 'u1', email: 'ayse@ravencia.com', displayName: 'Ayşe Yılmaz', role: 'OWNER', state: 'ACTIVE', tenantId: 't1' }

export async function installApiMock(page, { signedIn = true, unhandled = new Set(), overrides = [] } = {}) {
  await page.route('**/hubs/**', route => route.abort())
  await page.route('**/api/**', async route => {
    const request = route.request()
    const url = new URL(request.url())
    const path = url.pathname.replace(/^\/api\/v1/, '')
    const method = request.method()
    const json = (body, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
    if (path === '/auth/csrf') return json({ token: 'csrf' })
    if (path === '/auth/me') return signedIn ? json(me) : json({ title: 'Oturum yok' }, 401)
    for (const [matcher, answer] of [...overrides, ...fixtures]) {
      const [matchMethod, pattern] = matcher.includes(' ') ? matcher.split(' ') : ['GET', matcher]
      if (matchMethod !== method) continue
      const match = typeof pattern === 'string' && pattern.startsWith('^') ? path.match(new RegExp(pattern)) : path === pattern ? [path] : null
      if (!match) continue
      const body = typeof answer === 'function' ? answer({ url, path, match, body: request.postData() }) : answer
      return body === undefined ? route.fulfill({ status: 204 }) : json(body)
    }
    unhandled.add(`${method} ${path}`)
    return method === 'GET' ? json(empty) : route.fulfill({ status: 204 })
  })
}
