import { describe, expect, it } from 'vitest'
import { connectionEnvironmentLabel, credentialRequestBody, readIntegrationListState, stageTestOrderAvailable, writeIntegrationListState } from './integration-helpers'

describe('integration helpers', () => {
  it('builds the credential body of each platform', () => {
    const data = new FormData()
    data.set('apiKey', 'key'); data.set('apiSecret', 'secret'); data.set('email', 'a@b.test'); data.set('password', '')
    expect(credentialRequestBody('N11', data)).toEqual({ apiKey: 'key', apiSecret: 'secret' })
    expect(credentialRequestBody('TRENDYOL_EFATURAM', data)).toEqual({ email: 'a@b.test', password: null })
  })

  it('names the environment in Turkish (W32)', () => {
    expect(connectionEnvironmentLabel('TRENDYOL', 'PRODUCTION')).toBe('Canlı')
    expect(connectionEnvironmentLabel('TRENDYOL', 'STAGE')).toBe('Test Ortamı')
    expect(connectionEnvironmentLabel('SHOPIFY', 'STAGE')).toBe('Canlı')
  })

  it('offers the test order on every Trendyol or Hepsiburada test connection, without a fixed store number (W32)', () => {
    expect(stageTestOrderAvailable({ platformCode: 'TRENDYOL', environment: 'STAGE' })).toBe(true)
    expect(stageTestOrderAvailable({ platformCode: 'HEPSIBURADA', environment: 'STAGE' })).toBe(true)
    expect(stageTestOrderAvailable({ platformCode: 'TRENDYOL', environment: 'PRODUCTION' })).toBe(false)
    expect(stageTestOrderAvailable({ platformCode: 'N11', environment: 'STAGE' })).toBe(false)
  })

  it('reads and writes the list filters in the address (W16)', () => {
    expect(readIntegrationListState(new URLSearchParams())).toEqual({ view: 'ALL', filter: 'ALL', platform: 'ALL' })
    expect(readIntegrationListState(new URLSearchParams('gorunum=bagli&durum=dikkat&platform=hepsiburada'))).toEqual({ view: 'ACTIVE', filter: 'ATTENTION', platform: 'HEPSIBURADA' })
    expect(readIntegrationListState(new URLSearchParams('durum=x&platform=amazon'))).toEqual({ view: 'ALL', filter: 'ALL', platform: 'ALL' })
    expect(writeIntegrationListState(new URLSearchParams('gorunum=bagli'), { platform: 'TRENDYOL_EFATURAM' }).toString()).toBe('gorunum=bagli&platform=trendyol_efaturam')
    expect(writeIntegrationListState(new URLSearchParams('gorunum=bagli&durum=aktif'), { view: 'ALL', filter: 'ALL' }).toString()).toBe('')
  })
})
