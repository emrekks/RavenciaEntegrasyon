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

  it('keeps the editor colour, size and alignment styles on text', () => {
    expect(sanitizeRichText('<p style="text-align: center">Orta</p>')).toBe('<p style="text-align: center">Orta</p>')
    expect(sanitizeRichText('<span style="color: rgb(118, 82, 232);">Mor</span>')).toBe('<span style="color: rgb(118, 82, 232)">Mor</span>')
    expect(sanitizeRichText('<span style="font-size: x-large; color:#7652e8">Büyük</span>')).toBe('<span style="font-size: x-large; color: #7652e8">Büyük</span>')
  })

  it('drops every other style property and any unsafe value', () => {
    expect(sanitizeRichText('<p style="position:fixed; top:0; text-align:right">x</p>')).toBe('<p style="text-align: right">x</p>')
    for (const style of ['color: red; background: url(https://evil.example.test/x.png)', 'color: expression(alert(1))', 'color: re\\64', 'color: red /* x */', 'font-size: 12px; behavior: url(x.htc)', 'color: javascript:alert(1)', 'text-align: inherit', 'font-size: calc(1px + 2px)']) {
      const result = sanitizeRichText(`<span style="${style}">x</span>`)
      expect(result, style).not.toMatch(/url|expression|behavior|javascript|\\|calc|inherit|\/\*/i)
    }
    expect(sanitizeRichText('<span style="color: expression(alert(1))">x</span>')).toBe('<span>x</span>')
    expect(sanitizeRichText('<img src="https://cdn.example.test/a.jpg" style="color:red">')).toBe('<img src="https://cdn.example.test/a.jpg">')
  })

  it('rejects active and protocol-relative resources', () => {
    const result = sanitizeRichText('<iframe src="https://evil.example.test"></iframe><img src="data:text/html,<script>alert(1)</script>"><a href="//evil.example.test">Kötü bağlantı</a><a href="/catalog/item">Göreli bağlantı</a>')

    expect(result).not.toContain('<iframe')
    expect(result).not.toContain('data:')
    expect(result).not.toContain('//evil.example.test')
    expect(result).toContain('href="/catalog/item"')
  })
})
