import { describe, expect, it } from 'vitest'
import { productDeleteJobNote, productDeleteResultMessage } from './product-delete-text'

describe('catalog delete texts', () => {
  it('names the jobs the delete cancels, and nothing when there are none', () => {
    expect(productDeleteJobNote(3)).toBe('3 bekleyen yayın veya fiyat-stok işi iptal edilecek; pazar yerine bir şey gönderilmez.')
    expect(productDeleteJobNote(0)).toBeNull()
    expect(productDeleteJobNote(undefined)).toBeNull()
  })

  it('counts the records and the cancelled jobs over every request', () => {
    expect(productDeleteResultMessage([{ deletedCount: 500, cancelledJobCount: 2 }, { deletedCount: 19, cancelledJobCount: 1 }], 519)).toBe('519 katalog kaydı yerel katalogdan silindi. 3 bekleyen iş iptal edildi.')
    expect(productDeleteResultMessage([{ deletedCount: 1, cancelledJobCount: 0 }], 1)).toBe('Ürün ve varyantları yerel katalogdan silindi.')
    expect(productDeleteResultMessage([{}], 2)).toBe('2 katalog kaydı yerel katalogdan silindi.')
  })
})
