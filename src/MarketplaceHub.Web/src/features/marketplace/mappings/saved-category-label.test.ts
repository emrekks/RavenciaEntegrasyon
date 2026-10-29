import { describe, expect, it } from 'vitest'
import { savedCategoryLabel } from './saved-category-label'

describe('saved category label', () => {
  it('shows the name once it is known, even while a refetch runs', () => {
    expect(savedCategoryLabel('Moda / Kadın Giyim / Kadın Bluz', '1234', false)).toEqual({ state: 'resolved', text: 'Moda / Kadın Giyim / Kadın Bluz' })
    expect(savedCategoryLabel('Moda / Kadın Bluz', '1234', true)).toEqual({ state: 'resolved', text: 'Moda / Kadın Bluz' })
  })

  it('waits instead of showing the raw id while the list loads', () => {
    expect(savedCategoryLabel(undefined, '1234', true)).toEqual({ state: 'loading' })
    expect(savedCategoryLabel('  ', '1234', true)).toEqual({ state: 'loading' })
  })

  it('marks an id it could not resolve as a fallback', () => {
    expect(savedCategoryLabel(undefined, '1234', false)).toEqual({ state: 'fallback', text: 'Kategori #1234' })
    expect(savedCategoryLabel(null, '', false)).toEqual({ state: 'fallback', text: 'Kategori bilinmiyor' })
  })
})
