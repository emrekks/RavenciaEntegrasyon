import { describe, expect, it } from 'vitest'
import { sortByTurkishName } from './attribute-sorting'

describe('sortByTurkishName', () => {
  it('sorts names by Turkish alphabetical order without mutating the source', () => {
    const items = ['Yaş Grubu', 'Menşei', 'Kumaş Tipi', 'Kol Boyu', 'Kalıp', 'Kalınlık', 'Ek Özellik', 'Desen', 'Cinsiyet', 'Cep', 'Boy']
      .map(name => ({ name }))

    const sorted = sortByTurkishName(items)

    expect(sorted.map(item => item.name)).toEqual(['Boy', 'Cep', 'Cinsiyet', 'Desen', 'Ek Özellik', 'Kalınlık', 'Kalıp', 'Kol Boyu', 'Kumaş Tipi', 'Menşei', 'Yaş Grubu'])
    expect(items[0].name).toBe('Yaş Grubu')
  })
})
