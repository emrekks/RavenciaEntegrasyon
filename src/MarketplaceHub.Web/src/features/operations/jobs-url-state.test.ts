import { describe, expect, it } from 'vitest'
import { jobsLogsTabOpen, jobsMainTab, readJobsViewState, withJobsLogsTab, withJobsMainTab, writeJobsViewState } from './jobs-url-state'

describe('İşlem Takibi address state (W16)', () => {
  it('reads the defaults from an empty address', () => {
    expect(readJobsViewState(new URLSearchParams())).toEqual({ category: 'ALL', chips: [], status: '', timeRange: '24h', search: '', page: 1, pageSize: 20 })
  })

  it('reads every filter and the page back', () => {
    const state = readJobsViewState(new URLSearchParams('kategori=siparis&durum=hata,bekliyor&statu=dead&aralik=7g&ara=corr-1&sayfa=3&adet=50&platform=trendyol'))
    expect(state).toEqual({ category: 'ORDERS', chips: ['error', 'waiting'], status: 'DEAD', timeRange: '7d', search: 'corr-1', page: 3, pageSize: 50 })
  })

  it('keeps the overview link ?durum=hata working', () => {
    expect(readJobsViewState(new URLSearchParams('durum=hata')).chips).toEqual(['error'])
  })

  it('ignores values it does not know', () => {
    const state = readJobsViewState(new URLSearchParams('kategori=x&durum=y&statu=Z&aralik=1y&sayfa=-2&adet=7'))
    expect(state).toEqual({ category: 'ALL', chips: [], status: '', timeRange: '24h', search: '', page: 1, pageSize: 20 })
  })

  it('writes only what differs from the default, keeps the platform and goes back to page 1 on a filter change', () => {
    const current = new URLSearchParams('platform=n11&sayfa=4')
    expect(writeJobsViewState(current, { category: 'INVOICES' }).toString()).toBe('platform=n11&kategori=fatura')
    expect(writeJobsViewState(current, { page: 5 }).toString()).toBe('platform=n11&sayfa=5')
    expect(writeJobsViewState(new URLSearchParams('kategori=fatura'), { category: 'ALL', pageSize: 100 }).toString()).toBe('adet=100')
  })

  it('opens and closes the Loglar tab without touching the job filters', () => {
    const opened = withJobsLogsTab(new URLSearchParams('kategori=fatura&platform=n11'), true)
    expect(opened.toString()).toBe('kategori=fatura&platform=n11&sekme=loglar')
    expect(jobsLogsTabOpen(opened)).toBe(true)
    expect(readJobsViewState(opened).category).toBe('INVOICES')
    const closed = withJobsLogsTab(opened, false)
    expect(closed.toString()).toBe('kategori=fatura&platform=n11')
    expect(jobsLogsTabOpen(closed)).toBe(false)
    expect(jobsLogsTabOpen(new URLSearchParams('sekme=x'))).toBe(false)
  })

  it('reads and writes the three main tabs, an unknown one opening the job list', () => {
    const current = new URLSearchParams('kategori=fatura')
    expect(withJobsMainTab(current, 'health').toString()).toBe('kategori=fatura&sekme=api-saglik')
    expect(jobsMainTab(withJobsMainTab(current, 'health'))).toBe('health')
    expect(jobsMainTab(withJobsMainTab(current, 'logs'))).toBe('logs')
    expect(withJobsMainTab(new URLSearchParams('kategori=fatura&sekme=api-saglik'), 'jobs').toString()).toBe('kategori=fatura')
    expect(jobsMainTab(new URLSearchParams('sekme=x'))).toBe('jobs')
  })
})
