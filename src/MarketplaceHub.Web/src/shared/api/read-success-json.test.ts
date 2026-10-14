import { describe, expect, it } from 'vitest'
import { readSuccessJson } from './read-success-json'

describe('API success response parsing', () => {
  it('reads valid JSON responses', async () => {
    await expect(readSuccessJson(new Response('{"ok":true}'))).resolves.toEqual({ ok: true })
  })

  it('explains an empty response in plain Turkish', async () => {
    await expect(readSuccessJson(new Response(''))).rejects.toThrow('Sunucudan boş yanıt geldi. Sayfayı yenileyip tekrar deneyin.')
  })

  it('explains malformed JSON without leaking the browser parser error', async () => {
    await expect(readSuccessJson(new Response('<html>'))).rejects.toThrow('Sunucudan geçerli bir yanıt alınamadı. Sayfayı yenileyip tekrar deneyin.')
  })
})
