import { describe, expect, it } from 'vitest'
import { inferNotificationTarget } from './notifications'

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
