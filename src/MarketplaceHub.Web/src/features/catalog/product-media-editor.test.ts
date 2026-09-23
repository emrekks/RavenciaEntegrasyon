import { describe, expect, it } from 'vitest'
import { mediaRefsEqual, reorderMediaUrls } from './product-media-editor'

describe('product media editing', () => {
  it('does not import a family image when it is outside the editable product media list', () => {
    const productMedia = ['https://cdn.example.test/product.jpg']

    expect(reorderMediaUrls(productMedia, productMedia[0], 'https://cdn.example.test/family.jpg')).toBe(productMedia)
  })

  it('reorders only URLs owned by the editable media list', () => {
    expect(reorderMediaUrls(['first.jpg', 'second.jpg', 'third.jpg'], 'third.jpg', 'first.jpg'))
      .toEqual(['third.jpg', 'first.jpg', 'second.jpg'])
  })

  it('recognizes unchanged variant image references so they are not deleted and re-uploaded on a product save', () => {
    expect(mediaRefsEqual(['/api/v1/files/product-media/asset/content'], ['/api/v1/files/product-media/asset/content'])).toBe(true)
    expect(mediaRefsEqual([], ['/api/v1/files/product-media/asset/content'])).toBe(false)
    expect(mediaRefsEqual(['second.jpg', 'first.jpg'], ['first.jpg', 'second.jpg'])).toBe(false)
  })
})
