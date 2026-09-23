import { describe, expect, it } from 'vitest'
import { MAX_PRODUCT_MEDIA_URL_LENGTH, productMediaUrlIssue } from './product-media-url'

describe('productMediaUrlIssue', () => {
  it('accepts public HTTPS URLs at the server length limit', () => {
    const prefix = 'https://images.example.test/'
    const url = `${prefix}${'a'.repeat(MAX_PRODUCT_MEDIA_URL_LENGTH - prefix.length)}`
    expect(url).toHaveLength(MAX_PRODUCT_MEDIA_URL_LENGTH)
    expect(productMediaUrlIssue(url)).toBeNull()
  })

  it('rejects URLs that exceed the server length limit', () => {
    const prefix = 'https://images.example.test/'
    expect(productMediaUrlIssue(`${prefix}${'a'.repeat(MAX_PRODUCT_MEDIA_URL_LENGTH - prefix.length + 1)}`))
      .toContain('512 karakteri aşamaz')
  })

  it('rejects non-HTTPS and credential-bearing URLs', () => {
    expect(productMediaUrlIssue('http://images.example.test/item.jpg')).toContain('HTTPS')
    expect(productMediaUrlIssue('https://user:secret@images.example.test/item.jpg')).toContain('parola')
  })

  it('rejects local and private hosts before product creation', () => {
    expect(productMediaUrlIssue('https://localhost/item.jpg')).toContain('Yerel ağ')
    expect(productMediaUrlIssue('https://192.168.1.20/item.jpg')).toContain('IP adresleri')
    expect(productMediaUrlIssue('https://[::1]/item.jpg')).toContain('IP adresleri')
  })

  it('explains malformed URLs without exposing their contents', () => {
    expect(productMediaUrlIssue('not-a-url')).toContain('Geçerli')
  })
})
