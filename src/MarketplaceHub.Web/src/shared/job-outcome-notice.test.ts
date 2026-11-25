import { afterEach, describe, expect, it } from 'vitest'
import { jobOutcomeNotification } from './job-outcome-notice'
import { appendNotification, readNotificationHistory } from './notifications'

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
