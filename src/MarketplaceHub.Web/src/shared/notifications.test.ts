import { describe, expect, it } from 'vitest'
import { normalizeNotificationKind } from './notifications'

describe('notification tone normalization', () => {
  it('keeps queued and pending messages informational even when older callers marked them successful', () => {
    expect(normalizeNotificationKind('İade eşitlemesi kuyruğa alındı. İş tamamlandığında kayıtlar otomatik yenilenir.', 'success')).toBe('info')
    expect(normalizeNotificationKind('İade kabulü Trendyol’a gönderildi. Trendyol durumu: Trendyol incelemesi bekleniyor.', 'success')).toBe('info')
    expect(normalizeNotificationKind('Fatura sağlayıcıda işleniyor.', 'success')).toBe('info')
  })

  it('keeps failure messages red and completed messages green', () => {
    expect(normalizeNotificationKind('İade reddi Trendyol’a gönderildi.', 'success')).toBe('error')
    expect(normalizeNotificationKind('Fatura başarıyla oluşturuldu.', 'success')).toBe('success')
  })
})
