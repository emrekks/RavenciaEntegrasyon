import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { NotificationPanel } from './NotificationPanel'
import type { AppNotification } from '../shared/notifications'

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true

let root: Root | undefined
let host: HTMLDivElement | undefined

afterEach(() => {
  if (root) act(() => root?.unmount())
  host?.remove()
  root = undefined
  host = undefined
  localStorage.clear()
})

function render(notifications: AppNotification[], onNavigate = vi.fn()) {
  host = document.createElement('div')
  document.body.append(host)
  root = createRoot(host)
  act(() => root?.render(<NotificationPanel notifications={notifications} onNavigate={onNavigate} onCleared={() => undefined} />))
  return host
}

const now = new Date().toISOString()

describe('notification panel', () => {
  it('shows where it happened, what it was about and the specific title', () => {
    const panel = render([{
      id: 'a', message: 'Pazarama yayın durumu sorgulanamadı: Ürün bulunamadı.', kind: 'error', createdAt: now, read: false,
      title: 'Yayın Durumu Sorgulanamadı', platformCode: 'PAZARAMA', storeName: 'Ravencia', href: '/products/p1', actionLabel: 'Ürüne Git',
      subject: { kind: 'product', id: 'p1', label: 'Saten Elbise (MZ054)', href: '/products/p1' }
    }])
    expect(panel.querySelector('.rv-notification-source')?.textContent).toBe('Pazarama · Ravencia')
    expect(panel.querySelector<HTMLImageElement>('.rv-notification-logo')?.getAttribute('src')).toBe('/platforms/pazarama.webp')
    expect(panel.querySelector('.rv-notification-title')?.textContent).toBe('Yayın Durumu Sorgulanamadı')
    expect(panel.querySelector('.rv-notification-subject')?.textContent).toBe('ÜrünSaten Elbise (MZ054)')
    expect(panel.querySelector('.rv-notification-go')?.textContent).toBe('Ürüne Git')
  })

  it('opens a collapsed burst with its count and the records it touched', () => {
    const onNavigate = vi.fn()
    const panel = render([{
      id: 'b', message: 'Pazarama yayın durumu sorgulanamadı (p2).', kind: 'error', createdAt: now, read: false, title: 'Yayın Durumu Sorgulanamadı', platformCode: 'PAZARAMA',
      count: 3, groupMessage: '{count} ürün için Pazarama yayın durumu sorgulanamadı.',
      items: [{ kind: 'product', id: 'p2', label: 'Ürün B', href: '/products/p2' }, { kind: 'product', id: 'p1', label: 'Ürün A', href: '/products/p1' }]
    }], onNavigate)
    expect(panel.querySelector('.rv-notification-count')?.textContent).toBe('3×')
    expect(panel.querySelector('.rv-notification-history-item p')?.textContent).toBe('2 ürün için Pazarama yayın durumu sorgulanamadı.')
    act(() => panel.querySelector<HTMLButtonElement>('.rv-notification-more')?.click())
    const links = Array.from(panel.querySelectorAll<HTMLButtonElement>('.rv-notification-items button'))
    expect(links.map(link => link.textContent)).toEqual(['Ürün B', 'Ürün A'])
    act(() => links[1].click())
    expect(onNavigate).toHaveBeenCalledWith('/products/p1')
  })

  it('still renders a notification saved before titles and platforms existed', () => {
    const panel = render([{ id: 'c', message: 'Yayın durumunu sorgulamak için etkin Trendyol, Hepsiburada, Shopify veya N11 bağlantısı gerekir.', kind: 'error', createdAt: now, read: true, href: '/products', actionLabel: 'Ürünlere Git' }])
    expect(panel.querySelector('.rv-notification-title')?.textContent).toBe('Yayın Durumu Sorgulanamadı')
    expect(panel.querySelector('.rv-notification-source')?.textContent).toBe('Ürün Kataloğu')
    expect(panel.querySelector('.rv-notification-logo')).toBeNull()
  })
})
