import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiRequestError, backgroundPollInit, hubApi, hubPoll, isPermanentRequestError, isSignedOutProblem, isUserRecentlyActive, noteUserActivity, setStepUpHandler, setUnauthorizedHandler, trackUserActivity } from './client'

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

describe('a wrong password in a confirmation', () => {
  it('does not sign the person out (G5)', async () => {
    vi.stubGlobal('fetch', respond(401, { code: 'REAUTHENTICATION_FAILED', title: 'Parola doğrulanamadı.' }))
    const handler = vi.fn()
    const release = setUnauthorizedHandler(handler)
    await expect(hubApi('/invoices/1/documents/latest')).rejects.toMatchObject({ status: 401, code: 'REAUTHENTICATION_FAILED' })
    expect(handler).not.toHaveBeenCalled()
    release()
  })

  it('signs out only on an ended session', () => {
    expect(isSignedOutProblem(401, 'AUTHENTICATION_REQUIRED')).toBe(true)
    expect(isSignedOutProblem(401, 'MFA_ATTEMPTS_EXCEEDED')).toBe(true)
    expect(isSignedOutProblem(401, undefined)).toBe(true)
    expect(isSignedOutProblem(401, 'SECOND_FACTOR_FAILED')).toBe(false)
    expect(isSignedOutProblem(403, 'AUTHENTICATION_REQUIRED')).toBe(false)
  })
})

describe('irreversible work that needs a fresh sign-in', () => {
  it('asks the registered prompt and sends the same request again once', async () => {
    let resets = 0
    const fetchMock = vi.fn(async (url: string) => {
      if (url === '/api/v1/auth/csrf') return new Response(JSON.stringify({ token: 't' }), { status: 200 })
      resets++
      return resets === 1
        ? new Response(JSON.stringify({ code: 'REAUTHENTICATION_REQUIRED' }), { status: 403, headers: { 'Content-Type': 'application/json' } })
        : new Response(JSON.stringify({ done: true }), { status: 200, headers: { 'Content-Type': 'application/json' } })
    })
    vi.stubGlobal('fetch', fetchMock)
    const prompt = vi.fn(async () => true)
    const release = setStepUpHandler(prompt)
    await expect(hubApi('/settings/data-reset', { method: 'POST', body: '{"scopes":["ORDERS"]}' })).resolves.toEqual({ done: true })
    expect(prompt).toHaveBeenCalledTimes(1)
    expect(resets).toBe(2)
    release()
  })

  it('gives up when the person cancels the prompt', async () => {
    vi.stubGlobal('fetch', respond(403, { code: 'REAUTHENTICATION_REQUIRED', title: 'Yeniden doğrulama gerekir.' }))
    const release = setStepUpHandler(async () => false)
    await expect(hubApi('/settings/data-reset', { method: 'POST', body: '{}' })).rejects.toMatchObject({ status: 403, code: 'REAUTHENTICATION_REQUIRED' })
    release()
  })
})

describe('polls on a timer (W1)', () => {
  it('say they are automatic unless the person was just active', () => {
    noteUserActivity(Date.now() - 120_000)
    expect(new Headers(backgroundPollInit()?.headers).get('X-Background-Poll')).toBe('1')
    expect(new Headers(backgroundPollInit({ headers: { Accept: 'application/json' } })?.headers).get('Accept')).toBe('application/json')
    noteUserActivity()
    expect(new Headers(backgroundPollInit()?.headers).get('X-Background-Poll')).toBeNull()
  })

  it('notice key presses and clicks', () => {
    noteUserActivity(0)
    const release = trackUserActivity(window)
    window.dispatchEvent(new Event('keydown'))
    expect(isUserRecentlyActive()).toBe(true)
    release()
  })

  it('go through hubPoll with the header when nobody is there', async () => {
    noteUserActivity(0)
    const fetchMock = respond(200, { totalCount: 1 })
    vi.stubGlobal('fetch', fetchMock)
    await hubPoll('/orders/summary')
    const init = (fetchMock.mock.calls[0] as unknown[])[1] as RequestInit
    expect(new Headers(init.headers).get('X-Background-Poll')).toBe('1')
  })
})

describe('errors a repeat cannot fix', () => {
  it('are the signed-out, forbidden, missing, stale and invalid answers', () => {
    for (const status of [401, 403, 404, 412, 422]) expect(isPermanentRequestError(new ApiRequestError('x', status))).toBe(true)
    for (const status of [400, 408, 409, 429, 500, 502, 503]) expect(isPermanentRequestError(new ApiRequestError('x', status))).toBe(false)
    expect(isPermanentRequestError(new TypeError('Failed to fetch'))).toBe(false)
  })
})
