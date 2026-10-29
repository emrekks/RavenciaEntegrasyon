import { afterEach, describe, expect, it } from 'vitest'
import { appendNotification, inferNotificationTarget, readNotificationHistory, sentenceTitle, shouldRecordNotification } from './notifications'
import { scopedStorageKey } from './storage-scope'

describe('saved notification targets', () => {
  it('sends a connection notice saved with the old Returns link to Integrations, and keeps chosen links', () => {
    localStorage.setItem(scopedStorageKey('ravencia.notification-history'), JSON.stringify([
      { id: 'a', message: 'SIT HEPSİBURADA bağlantısında seçilen veriler sıfırlandı: sipariş verileri, iade verileri, fatura verileri, ürün verileri. Otomatik eşitleme açıksa kayıtlar yeniden çekilebilir.', kind: 'success', createdAt: '2026-10-22T00:04:00Z', read: false, href: '/returns', actionLabel: 'İadelere Git' },
      { id: 'b', message: '3 iade kabulü pazar yerine gönderildi.', kind: 'success', createdAt: '2026-10-22T00:05:00Z', read: false, href: '/returns?status=APPROVED', actionLabel: 'Onaylanan İadelere Git' }
    ]))
    const [reset, decision] = readNotificationHistory()
    expect([reset.href, reset.actionLabel]).toEqual(['/integrations', 'Entegrasyonlara Git'])
    expect([decision.href, decision.actionLabel]).toEqual(['/returns?status=APPROVED', 'Onaylanan İadelere Git'])
    localStorage.clear()
  })
})

describe('notification targets', () => {
  it('sends return decisions to the matching returns tab', () => {
    expect(inferNotificationTarget('3 iade kabulü pazar yerine gönderildi. Stoklar artırıldı.')).toEqual({ href: '/returns?status=APPROVED', label: 'Onaylanan İadelere Git' })
    expect(inferNotificationTarget('2 iade reddedildi, 1 iade reddedilemedi.')).toEqual({ href: '/returns?status=ACTION_REQUIRED', label: 'Bekleyen İadelere Git' })
    expect(inferNotificationTarget('2 bağlantıda iade eşitlemesi kuyruğa alındı.')).toEqual({ href: '/returns', label: 'İadelere Git' })
  })

  it('sends connection and write-switch problems to the integrations page', () => {
    expect(inferNotificationTarget('Global veya connection dış yazma anahtarı kapalı.')?.href).toBe('/integrations')
    expect(inferNotificationTarget('SIT Hepsiburada bağlantısında seçilen veriler sıfırlandı: sipariş verileri, iade verileri, fatura verileri.')).toEqual({ href: '/integrations', label: 'Entegrasyonlara Git' })
  })

  it('maps the other areas and returns nothing for unrelated text', () => {
    expect(inferNotificationTarget('Fatura kesildi')?.href).toBe('/invoices')
    expect(inferNotificationTarget('Stok 1 adet artırıldı')?.href).toBe('/products')
    expect(inferNotificationTarget('Kategori eşleştirmesi kaydedildi')?.href).toBe('/mappings/categories')
    expect(inferNotificationTarget('Tema değiştirildi')).toBeNull()
  })
})

describe('which notifications are kept in the history', () => {
  it('keeps failures and consequential successes', () => {
    expect(shouldRecordNotification('Global veya connection dış yazma anahtarı kapalı.', 'error')).toBe(true)
    expect(shouldRecordNotification('3 iade kabulü pazar yerine gönderildi. Stoklar artırıldı.', 'success')).toBe(true)
    expect(shouldRecordNotification('Fatura Trendyol’a gönderildi.', 'success')).toBe(true)
    expect(shouldRecordNotification('Diğer aktif oturumlar sonlandırıldı.', 'success')).toBe(true)
    expect(shouldRecordNotification('2 iade onaylandı, 1 iade onaylanamadı. Ayrıntılar toplu işlem penceresinde.', 'error')).toBe(true)
  })

  it('leaves validation, local feedback and queue acknowledgements as toasts', () => {
    expect(shouldRecordNotification('Değer boş olamaz.', 'error')).toBe(false)
    expect(shouldRecordNotification('En az bir aktarım alanı seçin.', 'error')).toBe(false)
    expect(shouldRecordNotification('Ravencia — Aydınlık önizlemeye uygulandı.', 'success')).toBe(false)
    expect(shouldRecordNotification('216 ürün kartı · 953 katalog kaydı tüm sayfalardan seçildi.', 'success')).toBe(false)
    expect(shouldRecordNotification('Kategori eşleştirmesi kaydedildi.', 'success')).toBe(false)
    expect(shouldRecordNotification('2 bağlantıda iade eşitlemesi kuyruğa alındı.', 'info')).toBe(false)
    expect(shouldRecordNotification('Seçilen varyantlar güncelleniyor…', 'info')).toBe(false)
  })
})

describe('notification kind of a count', () => {
  it('reads "0 başarısız" as no failure and any other count as one', async () => {
    const { normalizeNotificationKind } = await import('./notifications')
    expect(normalizeNotificationKind('3 siparişin faturası işlendi: 3 başarılı, 0 başarısız.', 'success')).toBe('success')
    expect(normalizeNotificationKind('3 siparişin faturası işlendi: 2 başarılı, 1 başarısız.', 'success')).toBe('error')
    expect(normalizeNotificationKind('10 başarısız kayıt var.', 'success')).toBe('error')
  })
})

describe('structured notifications', () => {
  afterEach(() => localStorage.clear())

  it('keeps the title, marketplace, store and subject, and leads to the subject', () => {
    appendNotification({ title: 'Yayın Durumu Sorgulanamadı', message: 'Pazarama yayın durumu sorgulanamadı: Ürün bulunamadı.', kind: 'error', platformCode: 'pazarama', storeName: 'Ravencia', code: 'NOT_FOUND', subject: { kind: 'product', id: 'p1', label: 'Saten Elbise (MZ054)', href: '/products/p1' } })
    const [saved] = readNotificationHistory()
    expect(saved).toMatchObject({ title: 'Yayın Durumu Sorgulanamadı', platformCode: 'PAZARAMA', storeName: 'Ravencia', code: 'NOT_FOUND', href: '/products/p1', actionLabel: 'Ürüne Git', subject: { kind: 'product', label: 'Saten Elbise (MZ054)' } })
  })

  it('records a producer’s notice on purpose even when its wording would stay a toast', () => {
    appendNotification({ title: 'Bağlantı Testi Başarılı', message: 'Pazarama mağazasına erişim doğrulandı.', kind: 'success', record: true, connectionId: 'c1' })
    expect(readNotificationHistory()).toHaveLength(1)
  })

  it('collapses a burst of the same failure into one notice with a count and the records it touched', () => {
    for (const id of ['p1', 'p2', 'p3', 'p1']) {
      appendNotification({ title: 'Yayın Durumu Sorgulanamadı', message: `Pazarama yayın durumu sorgulanamadı (${id}).`, kind: 'error', groupKey: 'publication-refresh-failed:c1:NOT_FOUND', groupMessage: '{count} ürün için Pazarama yayın durumu sorgulanamadı.', subject: { kind: 'product', id, label: id, href: `/products/${id}` } })
    }
    const history = readNotificationHistory()
    expect(history).toHaveLength(1)
    expect(history[0].count).toBe(4)
    expect(history[0].items?.map(item => item.id)).toEqual(['p1', 'p3', 'p2'])
  })

  it('collapses the identical notices saved before grouping existed, and still reads them', () => {
    const message = 'Yayın durumunu sorgulamak için etkin Trendyol, Hepsiburada, Shopify veya N11 bağlantısı gerekir.'
    localStorage.setItem(scopedStorageKey('ravencia.notification-history'), JSON.stringify([
      ...Array.from({ length: 17 }, (_, index) => ({ id: `n${index}`, message, kind: 'error', createdAt: new Date(Date.UTC(2026, 9, 28, 13, 48, 30 - index)).toISOString(), read: false })),
      { id: 'other', message: 'Fatura oluşturulamadı.', kind: 'error', createdAt: '2026-10-28T10:00:00Z', read: true }
    ]))
    const history = readNotificationHistory()
    expect(history).toHaveLength(2)
    expect(history[0]).toMatchObject({ id: 'n0', count: 17, firstAt: '2026-10-28T13:48:14.000Z' })
    expect(history[1]).toMatchObject({ id: 'other', read: true })
  })

  it('names the operation a short failure sentence describes', () => {
    expect(sentenceTitle('Bağlantı pasife alınamadı.')).toBe('Bağlantı Pasife Alınamadı')
    expect(sentenceTitle('Fiyat ve stok gönderimi başlatılamadı.')).toBe('Fiyat ve Stok Gönderimi Başlatılamadı')
    expect(sentenceTitle('İade kararı başlatılamadı; ayrıntı İşlem Takibi’nde.')).toBeUndefined()
  })
})
