import { describe, expect, it } from 'vitest'
import { sanitizeRichText } from './sanitizeHtml'

describe('sanitizeRichText', () => {
  it('keeps supported formatting while removing executable markup and handlers', () => {
    const result = sanitizeRichText('<p><strong>Güvenli</strong> metin</p><script>alert(1)</script><img src="https://cdn.example.test/item.jpg" onerror="alert(2)" style="color:red"><a href="javascript:alert(3)" onclick="alert(4)">Bağlantı</a>')

    expect(result).toContain('<strong>Güvenli</strong>')
    expect(result).toContain('<img src="https://cdn.example.test/item.jpg">')
    expect(result).not.toContain('<script')
    expect(result).not.toContain('onerror')
    expect(result).not.toContain('onclick')
    expect(result).not.toContain('javascript:')
    expect(result).not.toContain('style=')
  })

  it('rejects active and protocol-relative resources', () => {
    const result = sanitizeRichText('<iframe src="https://evil.example.test"></iframe><img src="data:text/html,<script>alert(1)</script>"><a href="//evil.example.test">Kötü bağlantı</a><a href="/catalog/item">Göreli bağlantı</a>')

    expect(result).not.toContain('<iframe')
    expect(result).not.toContain('data:')
    expect(result).not.toContain('//evil.example.test')
    expect(result).toContain('href="/catalog/item"')
  })
})
