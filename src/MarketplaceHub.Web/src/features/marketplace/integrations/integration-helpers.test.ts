import { describe, expect, it } from 'vitest'
import { connectionEnvironmentLabel, credentialRequestBody, readIntegrationListState, stageTestOrderAvailable, trendyolUserAgentNotice, writeIntegrationListState } from './integration-helpers'

describe('integration helpers', () => {
  it('builds the credential body of each platform', () => {
    const data = new FormData()
    data.set('apiKey', 'key'); data.set('apiSecret', 'secret'); data.set('email', 'a@b.test'); data.set('password', '')
    expect(credentialRequestBody('N11', data)).toEqual({ apiKey: 'key', apiSecret: 'secret' })
    // PTT AVM's rotation form asks for the API user name (apiKey field) and the API password (apiSecret field).
    expect(credentialRequestBody('PTTAVM', data)).toEqual({ pttAvmUsername: 'key', pttAvmPassword: 'secret' })
    expect(credentialRequestBody('TRENDYOL_EFATURAM', data)).toEqual({ email: 'a@b.test', password: null })
  })

  it('names the environment in Turkish (W32)', () => {
    expect(connectionEnvironmentLabel('TRENDYOL', 'PRODUCTION')).toBe('Canlı')
    expect(connectionEnvironmentLabel('TRENDYOL', 'STAGE')).toBe('Test Ortamı')
    expect(connectionEnvironmentLabel('SHOPIFY', 'STAGE')).toBe('Canlı')
    expect(connectionEnvironmentLabel('PTTAVM', 'STAGE')).toBe('Canlı')
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
    expect(readIntegrationListState(new URLSearchParams('platform=pttavm')).platform).toBe('PTTAVM')
    expect(writeIntegrationListState(new URLSearchParams('gorunum=bagli'), { platform: 'TRENDYOL_EFATURAM' }).toString()).toBe('gorunum=bagli&platform=trendyol_efaturam')
    expect(writeIntegrationListState(new URLSearchParams('gorunum=bagli&durum=aktif'), { view: 'ALL', filter: 'ALL' }).toString()).toBe('')
  })
})

describe('trendyolUserAgentNotice', () => {
  it('accepts the documented format for the store id', () => {
    expect(trendyolUserAgentNotice('123456 - Ravencia', '123456')).toBeNull()
    expect(trendyolUserAgentNotice('123456 - Ravencia', '')).toBeNull()
    expect(trendyolUserAgentNotice('', '123456')).toBeNull()
  })
  it('warns without blocking for another format or seller', () => {
    expect(trendyolUserAgentNotice('Firma - Entegrasyon', '123456')?.kind).toBe('warning')
    expect(trendyolUserAgentNotice('654321 - Ravencia', '123456')?.kind).toBe('warning')
    expect(trendyolUserAgentNotice('123456 - Ravencia Entegrasyon', '123456')?.kind).toBe('warning')
  })
  it('reports an identity that cannot be sent as a header', () => {
    expect(trendyolUserAgentNotice('123456 - Gümüş', '123456')?.kind).toBe('error')
  })
})
