import { describe, expect, it } from 'vitest'
import { inferNotificationTarget, shouldRecordNotification } from './notifications'

describe('notification targets', () => {
  it('sends return decisions to the matching returns tab', () => {
    expect(inferNotificationTarget('3 iade kabulü pazar yerine gönderildi. Stoklar artırıldı.')).toEqual({ href: '/returns?status=APPROVED', label: 'Onaylanan İadelere Git' })
    expect(inferNotificationTarget('2 iade reddedildi, 1 iade reddedilemedi.')).toEqual({ href: '/returns?status=ACTION_REQUIRED', label: 'Bekleyen İadelere Git' })
    expect(inferNotificationTarget('2 bağlantıda iade eşitlemesi kuyruğa alındı.')).toEqual({ href: '/returns', label: 'İadelere Git' })
  })

  it('sends connection and write-switch problems to the integrations page', () => {
    expect(inferNotificationTarget('Global veya connection dış yazma anahtarı kapalı.')?.href).toBe('/integrations')
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
