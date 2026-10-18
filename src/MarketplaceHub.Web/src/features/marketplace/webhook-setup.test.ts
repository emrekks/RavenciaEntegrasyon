import { describe, expect, it } from 'vitest'
import { webhookSetupText } from './webhook-setup'

describe('webhook setup text', () => {
  it('lists the address, Basic Auth credentials and paths for Hepsiburada', () => {
    const text = webhookSetupText({ environment: 'Test (SIT)', baseUrl: 'https://panel.ravencia.com/api/v1/hooks/id/token', username: 'ravencia-ab12', password: 'secret', paths: ['POST /orders', 'PUT /claims/accept'] })
    expect(text).toBe([
      'Hepsiburada webhook tanımı (Test (SIT))',
      'Base URL: https://panel.ravencia.com/api/v1/hooks/id/token',
      'Kimlik doğrulama: Basic Auth',
      'Kullanıcı adı: ravencia-ab12',
      'Şifre: secret',
      'Yollar:',
      '  POST /orders',
      '  PUT /claims/accept'
    ].join('\n'))
  })
})
