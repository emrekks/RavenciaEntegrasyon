import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

vi.mock('../../shared/api', async importOriginal => ({ ...await importOriginal<object>(), loadAllPages: async () => ({ items: [{ id: 's1', platformCode: 'TRENDYOL', displayName: 'Ravencia', status: 'ACTIVE', environment: 'PRODUCTION' }] }) }))
vi.mock('../marketplace/integrations/CommissionTariffsPanel', () => ({ commissionTariffPlatforms: new Set(['TRENDYOL']), tariffRowsQuery: () => ({}), CommissionTariffsPanel: () => <p>tarife</p> }))
vi.mock('../marketplace/integrations/commission-tariff-rows', () => ({ prefetchLastTariff: () => undefined }))
vi.mock('../marketplace/integrations/PromotionOffersPanel', () => ({ promotionTitles: { PLUS: 'Plus Komisyon Tarifesi', ADVANTAGE: 'Avantajlı Ürün Etiketi', FLASH: 'Flash Ürünler' }, PromotionOffersPanel: ({ kind }: { kind: string }) => <p>{kind}</p> }))

const { PromotionsPage } = await import('./PromotionsPage')

let host: HTMLDivElement | null = null
afterEach(() => { host?.remove(); host = null })

describe('promotions page tabs', () => {
  it('shows the platform as the page tab and the promotion pages as a segmented tablist', async () => {
    host = document.createElement('div'); document.body.append(host)
    const root = createRoot(host)
    await act(async () => { root.render(<QueryClientProvider client={new QueryClient()}><MemoryRouter initialEntries={['/promotions?tab=plus']}><PromotionsPage /></MemoryRouter></QueryClientProvider>) })
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 0)) })
    const platforms = host.querySelector('.promotion-platform-tabs [role="tablist"]')!
    expect(platforms.getAttribute('aria-label')).toBe('Pazaryeri')
    expect(platforms.classList.contains('rv-view-tabs')).toBe(true)
    expect(platforms.querySelector('[role="tab"][aria-selected="true"]')?.textContent).toBe('Trendyol')
    const pages = host.querySelector('.promotion-sections')!
    expect(pages.getAttribute('role')).toBe('tablist')
    expect(pages.classList.contains('reports-segmented')).toBe(true)
    expect([...pages.querySelectorAll('[role="tab"]')].map(tab => tab.textContent)).toEqual(['Ürün Komisyon Tarifesi', 'Plus Komisyon Tarifesi', 'Avantajlı Ürün Etiketi', 'Flash Ürünler'])
    expect(pages.querySelector('[aria-selected="true"]')?.textContent).toBe('Plus Komisyon Tarifesi')
    await act(async () => { root.unmount() })
  })
})
