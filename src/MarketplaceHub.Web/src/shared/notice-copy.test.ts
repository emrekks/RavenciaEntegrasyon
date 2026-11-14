import { describe, expect, it } from 'vitest'
import { composeNotice, titleFromSentence } from './notice-copy'
import { describeNotification, notificationItemList, presentNotification } from './notification-describe'
import type { AppNotification } from './notifications'

describe('notice wording policy', () => {
  it('drops a body that only repeats its title', () => {
    expect(composeNotice('success', 'Bağlantı Etkinleştirildi', 'Bağlantı etkinleştirildi.')).toEqual({ title: 'Bağlantı Etkinleştirildi', body: '' })
    expect(composeNotice('success', 'Ürün Kaydedildi', 'Ürün kaydedildi. Değişiklikler pazaryerlerine ayrıca gönderilir.')).toEqual({ title: 'Ürün Kaydedildi', body: 'Değişiklikler pazaryerlerine ayrıca gönderilir.' })
    expect(composeNotice('error', 'Durum Sorgulanamadı', 'Durum Sorgulanamadı: Ürün bulunamadı.')).toEqual({ title: 'Durum Sorgulanamadı', body: 'Ürün bulunamadı.' })
  })

  it('makes a short outcome sentence the title and keeps the rest as the body', () => {
    expect(composeNotice('success', undefined, 'Ürün kaydedildi.')).toEqual({ title: 'Ürün Kaydedildi', body: '' })
    expect(composeNotice('error', undefined, 'Sipariş listesi yüklenemedi. Bağlantıyı kontrol edin.')).toEqual({ title: 'Sipariş Listesi Yüklenemedi', body: 'Bağlantıyı kontrol edin.' })
    // An instruction is not an outcome: it stays the body under the generic title of its kind.
    expect(composeNotice('warning', undefined, 'Bir ürün seçin.')).toEqual({ title: 'Uyarı', body: 'Bir ürün seçin.' })
    expect(titleFromSentence('Kayıt adı zorunludur.')).toBeUndefined()
  })

  it('reads a long parenthetical as its own clause', () => {
    expect(composeNotice('error', 'Webhook Açılamadı', 'Webhook açılamadı (Shopify erişim anahtarı reddetti). Tekrar deneyin.').body).toBe('Shopify erişim anahtarı reddetti. Tekrar deneyin.')
  })
})

const card = (patch: Partial<AppNotification>): AppNotification => ({ id: 'n1', message: 'x', kind: 'success', createdAt: '2026-11-14T10:00:00.000Z', read: false, ...patch })

describe('notification detail rows', () => {
  it('names who did it, from where, the records with their result, the status change and the job', () => {
    const items = Array.from({ length: 12 }, (_, index) => ({ kind: 'order' as const, id: `o${index}`, label: `#${index}`, href: `/orders?search=${index}`, result: index === 0 ? 'error' as const : index === 1 ? 'skipped' as const : 'success' as const, reason: index === 0 ? 'Sipariş iptal edilmiş.' : undefined }))
    const notification = card({ title: 'Fatura Durumu Güncellendi', message: '12 sipariş “Faturası yüklendi” olarak işaretlendi.', actor: 'Ayşe Yılmaz', source: 'Faturalar', items, statusChange: { from: 'Fatura bekliyor', to: 'Faturası yüklendi' }, jobId: 'job-7', durationMs: 2300 })
    const rows = describeNotification(notification)
    const value = (label: string) => rows.find(row => row.label === label)
    expect(value('Kullanıcı')?.value).toBe('Ayşe Yılmaz')
    expect(value('Kaynak')?.value).toBe('Faturalar Sayfası')
    expect(value('Etkilenen Kayıt')?.value).toBe('12 sipariş · 10 başarılı, 1 atlandı, 1 hatalı')
    expect(value('Durum')?.value).toBe('Fatura bekliyor → Faturası yüklendi')
    expect(value('İlgili İş')).toMatchObject({ href: '/jobs?ara=job-7' })
    expect(value('Süre')?.value).toBe('2,3 sn')
    const list = notificationItemList(notification)
    expect(list.shown).toHaveLength(10)
    expect(list.more).toBe(2)
    expect(list.shown[0].note).toBe('Hata: Sipariş iptal edilmiş')
    expect(list.shown[1].note).toBe('Atlandı')
  })

  it('says a background notice is automatic and never repeats the title in the summary', () => {
    const notification = card({ title: 'Fatura Durumu Güncellendi', message: 'Fatura durumu güncellendi.', source: 'Otomatik' })
    expect(describeNotification(notification).find(row => row.label === 'Başlatan')?.value).toContain('Otomatik')
    expect(presentNotification(notification).summary).toBe('')
  })
})
