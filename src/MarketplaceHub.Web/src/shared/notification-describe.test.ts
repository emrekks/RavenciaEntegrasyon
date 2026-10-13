import { describe, expect, it } from 'vitest'
import { describeNotification } from './notification-describe'
import type { AppNotification } from './notifications'

const make = (message: string, kind: AppNotification['kind'], href?: string): AppNotification => ({ id: '1', message, kind, createdAt: '2026-10-13T00:00:00Z', read: false, href })

describe('describing a notification', () => {
  it('names the operation, area, marketplace and outcome of a queued return sync', () => {
    const facts = describeNotification(make('2 bağlantıda iade eşitlemesi kuyruğa alındı. İş tamamlandığında tüm iadeler otomatik yenilenir.', 'info', '/returns'))
    expect(facts.find(fact => fact.label === 'İşlem')?.value).toContain('İade senkronizasyonu')
    expect(facts.find(fact => fact.label === 'Alan')?.value).toBe('İade Yönetimi')
    expect(facts.find(fact => fact.label === 'Sonuç')?.value).toContain('İşlem Takibi')
  })

  it('marks failures and names the marketplace in the text', () => {
    const facts = describeNotification(make('İade kabulü Hepsiburada’a gönderildi. Stok 1 adet artırıldı.', 'success', '/returns?status=APPROVED'))
    expect(facts.find(fact => fact.label === 'Pazar Yeri')?.value).toBe('Hepsiburada')
    const failure = describeNotification(make('Global veya connection dış yazma anahtarı kapalı.', 'error', '/integrations'))
    expect(failure.find(fact => fact.label === 'Sonuç')).toMatchObject({ tone: 'error' })
    expect(failure.find(fact => fact.label === 'Alan')?.value).toBe('Entegrasyonlar')
  })
})
