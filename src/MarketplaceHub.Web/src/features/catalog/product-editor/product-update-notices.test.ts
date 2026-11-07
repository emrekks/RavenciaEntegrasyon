import { afterEach, describe, expect, it } from 'vitest'
import { readNotificationHistory } from '../../../shared/notifications'
import { recordProductUpdateNotices } from './product-update-notices'

describe('product update notices', () => {
  afterEach(() => localStorage.clear())

  it('keeps each card value the update sent in the notification center, naming product and marketplace', () => {
    const notice = 'RYP003T (Kadın Haki Kazak) · Trendyol: pazaryerinden oluşturulan yayın profilinde Teslimat Süresi yok; ürün kartındaki 3 gün gönderildi.'
    const count = recordProductUpdateNotices({ jobId: 'job-1', notices: [notice, '  '] }, { productId: 'p-1', productName: 'Kadın Haki Kazak', modelCode: 'RYP003T', platformCode: 'TRENDYOL', connectionId: 'c-1' })
    expect(count).toBe(1)
    const [entry] = readNotificationHistory()
    expect(entry.message).toBe(notice)
    expect(entry.title).toBe('Ürün Kartındaki Değer Gönderildi')
    expect(entry.kind).toBe('info')
    expect(entry.platformCode).toBe('TRENDYOL')
    expect(entry.jobId).toBe('job-1')
    expect(entry.subject?.id).toBe('p-1')
  })

  it('records nothing when the server raised no notice', () => {
    expect(recordProductUpdateNotices({ jobId: 'job-2' }, { productId: 'p-1', productName: 'Kazak', connectionId: 'c-1' })).toBe(0)
    expect(recordProductUpdateNotices({ jobId: 'job-3', notices: [] }, { productId: 'p-1', productName: 'Kazak', connectionId: 'c-1' })).toBe(0)
    expect(readNotificationHistory()).toHaveLength(0)
  })
})
