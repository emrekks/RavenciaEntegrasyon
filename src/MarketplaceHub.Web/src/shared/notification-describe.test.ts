import { describe, expect, it } from 'vitest'
import { deriveNotificationTitle, describeNotification, platformNamedIn, presentNotification } from './notification-describe'
import type { AppNotification } from './notifications'

const make = (message: string, kind: AppNotification['kind'], href?: string, extra: Partial<AppNotification> = {}): AppNotification => ({ id: '1', message, kind, createdAt: '2026-10-13T00:00:00Z', read: false, href, ...extra })
const fact = (notification: AppNotification, label: string) => describeNotification(notification).find(item => item.label === label)

describe('describing a notification', () => {
  it('names the operation, area and outcome of a queued return sync', () => {
    const notification = make('2 bağlantıda iade eşitlemesi kuyruğa alındı. İş tamamlandığında tüm iadeler otomatik yenilenir.', 'info', '/returns')
    expect(fact(notification, 'İşlem')?.value).toBe('İade senkronizasyonu')
    expect(fact(notification, 'Bölüm')?.value).toBe('İade Yönetimi')
    expect(fact(notification, 'Sonuç')?.value).toContain('İşlem Takibi')
  })

  it('names the one marketplace a sentence talks about, never one of a list', () => {
    expect(fact(make('İade kabulü Hepsiburada’a gönderildi. Stok 1 adet artırıldı.', 'success', '/returns?status=APPROVED'), 'Platform')?.value).toBe('Hepsiburada')
    expect(platformNamedIn('Yayın durumunu sorgulamak için etkin Trendyol, Hepsiburada, Shopify veya N11 bağlantısı gerekir.')).toBeNull()
    const failure = make('Global veya connection dış yazma anahtarı kapalı.', 'error', '/integrations')
    expect(fact(failure, 'Sonuç')).toMatchObject({ tone: 'error' })
    expect(fact(failure, 'Bölüm')?.value).toBe('Entegrasyonlar')
  })

  it('shows the structured rows of a notification that carries its context', () => {
    const notification = make('Pazarama yayın durumu sorgulanamadı: Ürün bulunamadı.', 'error', '/products/p1', {
      title: 'Yayın Durumu Sorgulanamadı', operation: 'Yayın durumu sorgulama', platformCode: 'PAZARAMA', storeName: 'Ravencia', code: 'PUBLICATION_STATE_REQUIRED', jobId: 'job-1',
      subject: { kind: 'product', id: 'p1', label: 'Saten Elbise (MZ054)', href: '/products/p1' }
    })
    const rows = describeNotification(notification)
    expect(rows.map(row => row.label)).toEqual(['Platform', 'Mağaza', 'İşlem', 'Ürün', 'Sonuç', 'Kod', 'İş No', 'Zaman'])
    expect(rows.find(row => row.label === 'Ürün')).toMatchObject({ value: 'Saten Elbise (MZ054)', href: '/products/p1' })
    const presentation = presentNotification(notification)
    expect([presentation.title, presentation.platformName, presentation.storeName]).toEqual(['Yayın Durumu Sorgulanamadı', 'Pazarama', 'Ravencia'])
  })
})

describe('titles of notifications without one', () => {
  it('names the operation and the outcome instead of a generic İşlem Başarısız', () => {
    expect(deriveNotificationTitle('Yayın durumunu sorgulamak için etkin Trendyol, Hepsiburada, Shopify veya N11 bağlantısı gerekir.', 'error')).toBe('Yayın Durumu Sorgulanamadı')
    expect(deriveNotificationTitle('Bağlantı testi başarılı; yetenek kontrolleri yenilendi. Dış yazma yapılmadı.', 'success')).toBe('Bağlantı Testi Başarılı')
    expect(deriveNotificationTitle('#123 nolu siparişin faturası oluşturulamadı: VKN geçersiz', 'error')).toBe('Fatura İşlemi Başarısız')
    expect(deriveNotificationTitle('Bağlantı pasife alınamadı.', 'error')).toBe('İşlem Başarısız')
  })

  it('reads the title the old toast bridge put in front of the sentence', () => {
    const presentation = presentNotification(make('Durum Sorgulanamadı: Pazarama: yayın durumu sorgulanamadı — Ürün bulunamadı.', 'error'))
    expect(presentation.title).toBe('Durum Sorgulanamadı')
    expect(presentation.summary).toBe('Pazarama: yayın durumu sorgulanamadı — Ürün bulunamadı.')
    expect(presentation.platformName).toBe('Pazarama')
  })

  it('summarises a burst that touched several records with its count', () => {
    const presentation = presentNotification(make('Pazarama yayın durumu sorgulanamadı: zaman aşımı', 'error', undefined, {
      title: 'Yayın Durumu Sorgulanamadı', count: 3, groupMessage: '{count} ürün için Pazarama yayın durumu sorgulanamadı.',
      items: [{ kind: 'product', label: 'A' }, { kind: 'product', label: 'B' }]
    }))
    expect(presentation.summary).toBe('2 ürün için Pazarama yayın durumu sorgulanamadı.')
    expect(presentation.count).toBe(3)
  })
})
