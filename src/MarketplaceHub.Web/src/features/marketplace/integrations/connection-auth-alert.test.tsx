import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { activationErrorMessage, ConnectionAuthAlert, connectionAuthProblem, reactivationNeedsTest } from './connection-auth-alert'

const base = { status: 'ACTIVE', lastErrorCode: null as string | null, lastErrorAt: null as string | null, lastTestedAt: '2026-10-19T08:00:00Z', lastSuccessAt: '2026-10-19T08:00:00Z', disabledAt: null as string | null }

describe('connectionAuthProblem', () => {
  it('sorts the refused-credential, access and gateway codes', () => {
    expect(connectionAuthProblem('PAZARAMA_CREDENTIALS_REJECTED')).toBe('credentials')
    expect(connectionAuthProblem('HEPSIBURADA_AUTHENTICATION_FAILED')).toBe('credentials')
    expect(connectionAuthProblem('N11_CREDENTIAL_FORMAT_INVALID')).toBe('credentials')
    expect(connectionAuthProblem('EFATURAM_ACCESS_TOKEN_REJECTED')).toBe('credentials')
    expect(connectionAuthProblem('REMOTE_AUTHENTICATION_FAILED')).toBe('credentials')
    expect(connectionAuthProblem('PAZARAMA_ACCESS_DENIED')).toBe('access')
    expect(connectionAuthProblem('N11_REST_ACCESS_DENIED')).toBe('access')
    expect(connectionAuthProblem('HEPSIBURADA_ACCESS_FORBIDDEN')).toBe('access')
    expect(connectionAuthProblem('SHOPIFY_REQUIRED_READ_SCOPE')).toBe('access')
    expect(connectionAuthProblem('PAZARAMA_GATEWAY_BLOCKED')).toBe('gateway')
  })

  it('ignores other errors and webhook signature failures', () => {
    expect(connectionAuthProblem(null)).toBeNull()
    expect(connectionAuthProblem('REMOTE_TIMEOUT')).toBeNull()
    expect(connectionAuthProblem('WEBHOOK_AUTHENTICATION_FAILED')).toBeNull()
    expect(connectionAuthProblem('SHOPIFY_WEBHOOK_SIGNATURE_INVALID')).toBeNull()
  })
})

describe('reactivationNeedsTest', () => {
  it('asks for a test newer than the disable', () => {
    expect(reactivationNeedsTest({ ...base, status: 'DISABLED', disabledAt: '2026-10-19T09:00:00Z' })).toBe(true)
    expect(reactivationNeedsTest({ ...base, status: 'DISABLED', disabledAt: '2026-10-19T07:00:00Z' })).toBe(false)
    expect(reactivationNeedsTest({ ...base, status: 'DISABLED', lastSuccessAt: null })).toBe(true)
    expect(reactivationNeedsTest({ ...base, disabledAt: '2026-10-19T09:00:00Z' })).toBe(false)
  })

  it('turns the API refusal into plain advice', () => {
    expect(activationErrorMessage(Object.assign(new Error('Devre dışı bırakılan bağlantıyı yeniden etkinleştirmeden önce yeni bir bağlantı testi gerekir.'), { code: 'CONNECTION_TEST_REQUIRED', status: 422 }))).toContain('Test Et’e basın')
    expect(activationErrorMessage(new Error('Başka bir hata.'))).toBe('Başka bir hata.')
    expect(activationErrorMessage('x')).toBe('Bağlantı etkinleştirilemedi.')
  })
})

describe('ConnectionAuthAlert', () => {
  let container: HTMLDivElement
  let root: Root

  beforeEach(() => {
    ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
    container = document.createElement('div')
    document.body.append(container)
    root = createRoot(container)
  })

  afterEach(() => {
    act(() => root.unmount())
    container.remove()
  })

  it('warns about refused credentials with the time and the platform hint, and starts a test', () => {
    const onTest = vi.fn()
    act(() => root.render(<ConnectionAuthAlert item={{ ...base, lastErrorCode: 'PAZARAMA_CREDENTIALS_REJECTED', lastErrorAt: '2026-10-20T06:30:00Z' }} detail="Pazarama Client Id ve Client Secret çiftini kabul etmedi." onTest={onTest} />))

    const alert = container.querySelector('[role="alert"]')!
    expect(alert.textContent).toContain('Kimlik bilgileri reddedildi')
    expect(alert.textContent).toContain('Test Et’e basın')
    expect(alert.textContent).toContain('Pazarama Client Id ve Client Secret çiftini kabul etmedi.')
    expect(alert.textContent).toContain('Son görülme: 20.10.2026')
    expect(alert.textContent).toContain('PAZARAMA_CREDENTIALS_REJECTED')
    act(() => container.querySelector<HTMLButtonElement>('.connection-auth-alert-test')!.click())
    expect(onTest).toHaveBeenCalledOnce()
  })

  it('falls back to the last test time and hides a hint that is only the code', () => {
    act(() => root.render(<ConnectionAuthAlert item={{ ...base, lastErrorCode: 'N11_REST_ACCESS_DENIED' }} detail="N11_REST_ACCESS_DENIED" />))

    const alert = container.querySelector('[role="alert"]')!
    expect(alert.textContent).toContain('Pazaryeri erişim izni vermiyor')
    expect(alert.textContent).toContain('Son görülme: 19.10.2026')
    expect(container.querySelector('.connection-auth-alert-hint')).toBeNull()
    expect(container.querySelector('.connection-auth-alert-test')).toBeNull()
  })

  it('shows the gateway block as a warning', () => {
    act(() => root.render(<ConnectionAuthAlert item={{ ...base, lastErrorCode: 'PAZARAMA_GATEWAY_BLOCKED' }} />))
    expect(container.querySelector('.connection-auth-alert.is-warning')!.textContent).toContain('geçici olarak engelliyor')
  })

  it('tells a disabled connection that it needs a fresh test before it is turned on again', () => {
    act(() => root.render(<ConnectionAuthAlert item={{ ...base, status: 'DISABLED', disabledAt: '2026-10-19T09:00:00Z' }} onTest={() => undefined} />))
    const note = container.querySelector('[role="status"]')!
    expect(note.textContent).toContain('Yeniden açmak için yeni test gerekli')
    expect(note.querySelector('button')!.textContent).toBe('Test Et')
  })

  it('renders nothing for a healthy connection', () => {
    act(() => root.render(<ConnectionAuthAlert item={{ ...base, lastErrorCode: 'REMOTE_TIMEOUT' }} />))
    expect(container.innerHTML).toBe('')
  })
})
