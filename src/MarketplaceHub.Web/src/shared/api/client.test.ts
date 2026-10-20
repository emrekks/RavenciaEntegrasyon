import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiRequestError, hubApi, isPermanentRequestError, setUnauthorizedHandler } from './client'

const respond = (status: number, body: unknown = {}) => vi.fn(async () => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }))

afterEach(() => { vi.unstubAllGlobals() })

describe('hubApi and an ended session', () => {
  it('reports a 401 to the app once per request and still rejects the call', async () => {
    vi.stubGlobal('fetch', respond(401, { title: 'Oturum gerekli.' }))
    const handler = vi.fn()
    const release = setUnauthorizedHandler(handler)
    await expect(hubApi('/orders')).rejects.toMatchObject({ status: 401 })
    expect(handler).toHaveBeenCalledTimes(1)
    release()
  })

  it('leaves other failures to the screen that asked', async () => {
    vi.stubGlobal('fetch', respond(502))
    const handler = vi.fn()
    const release = setUnauthorizedHandler(handler)
    await expect(hubApi('/orders')).rejects.toMatchObject({ status: 502 })
    expect(handler).not.toHaveBeenCalled()
    release()
  })

  it('forgets a handler that was released', async () => {
    vi.stubGlobal('fetch', respond(401))
    const handler = vi.fn()
    setUnauthorizedHandler(handler)()
    await expect(hubApi('/orders')).rejects.toBeInstanceOf(ApiRequestError)
    expect(handler).not.toHaveBeenCalled()
  })
})

describe('errors a repeat cannot fix', () => {
  it('are the signed-out, forbidden, missing, stale and invalid answers', () => {
    for (const status of [401, 403, 404, 412, 422]) expect(isPermanentRequestError(new ApiRequestError('x', status))).toBe(true)
    for (const status of [400, 408, 409, 429, 500, 502, 503]) expect(isPermanentRequestError(new ApiRequestError('x', status))).toBe(false)
    expect(isPermanentRequestError(new TypeError('Failed to fetch'))).toBe(false)
  })
})
