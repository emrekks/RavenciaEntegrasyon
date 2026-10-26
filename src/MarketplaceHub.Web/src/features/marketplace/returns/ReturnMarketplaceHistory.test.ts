import { describe, expect, it } from 'vitest'
import { acceptDetailText, claimStatusName } from './ReturnMarketplaceHistory'

describe('Trendyol return settlement labels', () => {
  it('names who accepted the line and keeps an unknown value as sent', () => {
    expect(acceptDetailText('DISPUTE')).toBe('İhtilaf sonucu kabul edildi')
    expect(acceptDetailText('supplier')).toBe('Satıcı onayladı')
    expect(acceptDetailText('SYSTEM')).toBe('Süre dolunca otomatik kabul edildi')
    expect(acceptDetailText('PARTNER')).toBe('PARTNER')
    expect(acceptDetailText(null)).toBeNull()
  })

  it('names the claim statuses of the history', () => {
    expect(claimStatusName('Unresolved')).toBe('İhtilafta')
    expect(claimStatusName('WaitingInAction')).toBe('Satıcı kararı bekleniyor')
    expect(claimStatusName('InAnalysis')).toBe('Trendyol incelemesinde')
    expect(claimStatusName(null)).toBe('—')
  })
})
