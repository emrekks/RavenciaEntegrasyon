import { act } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render } from '../../../test/render'
import { OneTimePasswordModal } from './UserManagementSection'

// Scan 25 Nov 2026 (D-5): the one-time password window closed on Escape (and on the backdrop), and the password, shown
// only once, was gone. Only “Parolayı Not Aldım” closes it now.
afterEach(cleanup)

const oneTime = { title: 'Şifre Sıfırlandı', email: 'kisi@example.com', password: 'fake-temp-123' }
const button = (label: string) => [...document.querySelectorAll<HTMLButtonElement>('button')].find(item => item.textContent?.trim() === label)

describe('OneTimePasswordModal', () => {
  it('stays open on Escape, the backdrop and X, and closes on Parolayı Not Aldım', async () => {
    const onClose = vi.fn()
    render(<OneTimePasswordModal oneTime={oneTime} onCopy={() => undefined} onClose={onClose} />)
    await act(async () => { document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })) })
    const overlay = document.querySelector<HTMLElement>('.rv-overlay')!
    await act(async () => { overlay.dispatchEvent(new MouseEvent('mousedown', { bubbles: true })) })
    await act(async () => { document.querySelector<HTMLButtonElement>('button[aria-label="Kapat"]')!.click() })
    expect(onClose).not.toHaveBeenCalled()
    expect(document.querySelector('[role=alert]')?.textContent).toContain('Parolayı Not Aldım')
    await act(async () => { button('Parolayı Not Aldım')!.click() })
    expect(onClose).toHaveBeenCalledTimes(1)
  })
})
