import { afterEach, describe, expect, it } from 'vitest'
import { followedOutcomeNotice, jobOutcomeNotification } from './job-outcome-notice'
import { appendNotification, noticeRecord, readNotificationHistory } from './notifications'

const jobs = [{ jobId: 'job-1', platformName: 'Trendyol', what: 'ürün aktarımı' }]

describe('followed job results in the notification history', () => {
  afterEach(() => localStorage.clear())

  it('keeps a partial transfer and a result waiting for review', () => {
    appendNotification(jobOutcomeNotification({ title: "Trendyol'a Kısmen Aktarıldı", kind: 'warning', lines: ['2 ürün aktarıldı, 1 ürün reddedildi.'], settled: true }, false, jobs))
    appendNotification(jobOutcomeNotification({ title: 'İnceleme Bekliyor', kind: 'warning', lines: ['Örnek ürün incelemede.'], settled: true }, false, jobs))
    const titles = readNotificationHistory().map(item => item.title)
    expect(titles).toContain("Trendyol'a Kısmen Aktarıldı")
    expect(titles).toContain('İnceleme Bekliyor')
  })

  it('keeps a finished transfer but leaves a still-running note as a toast', () => {
    appendNotification(jobOutcomeNotification({ title: "Trendyol'a Aktarıldı", kind: 'success', lines: ['Örnek ürün aktarıldı.'], settled: true }, false, jobs))
    appendNotification(jobOutcomeNotification({ title: 'Aktarım Sürüyor', kind: 'info', lines: ['Örnek ürün aktarılıyor.'], settled: false }, true, jobs))
    expect(readNotificationHistory().map(item => item.title)).toEqual(["Trendyol'a Aktarıldı"])
  })
})

// D-8 review: a bulk run's interim "Kısmen Aktarıldı" is not kept; only its final result is, once.
describe('bulk results in the notification history', () => {
  afterEach(() => localStorage.clear())

  it('keeps one entry for a bulk run: the final result, not the interim warning', () => {
    const interim = followedOutcomeNotice({ title: "Trendyol'a Kısmen Aktarıldı", kind: 'warning', lines: ['1 ürün aktarıldı, 1 ürün bekliyor.'], settled: false }, true)
    appendNotification({ title: interim.title, message: interim.message, kind: interim.kind, record: noticeRecord(interim.record, `${interim.title} ${interim.message}`, interim.kind, interim.persistent) })
    expect(readNotificationHistory()).toHaveLength(0)
    const final = followedOutcomeNotice({ title: "Trendyol'a Kısmen Aktarıldı", kind: 'warning', lines: ['1 ürün aktarıldı, 1 ürün reddedildi.'], settled: true }, false)
    appendNotification({ title: final.title, message: final.message, kind: final.kind, record: noticeRecord(final.record, `${final.title} ${final.message}`, final.kind, final.persistent) })
    expect(readNotificationHistory()).toHaveLength(1)
  })

  it('keeps a finished warning about the work but not validation or local feedback', () => {
    expect(noticeRecord(undefined, 'Toplu Yayın 2 kart atlandı, fiyatı yok.', 'warning')).toBe(true)
    expect(noticeRecord(undefined, 'Pazar yeri seçin.', 'warning')).toBeUndefined()
    expect(noticeRecord(undefined, 'Önizleme hazır, 2 uyarı var.', 'warning')).toBeUndefined()
    expect(noticeRecord(undefined, 'Yayın hazırlanıyor.', 'warning', true)).toBeUndefined()
    expect(noticeRecord(false, 'Toplu Yayın 2 kart atlandı.', 'warning')).toBe(false)
  })
})
