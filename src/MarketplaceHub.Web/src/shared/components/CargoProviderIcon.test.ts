import { describe, expect, it, vi } from 'vitest'
import { cargoCarrier, cargoLabel } from './CargoProviderIcon'

describe('hepsiJET carrier identity', () => {
  it.each(['hepsiJET', 'HEPSIJET', 'HEPSIJETMP', 'HepsiJet Kargo', 'D FAST DAĞITIM HİZMETLERİ VE LOJİSTİK ANONİM ŞİRKETİ'])('%s resolves to the shared carrier logo', value => {
    expect(cargoCarrier(value)).toMatchObject({
      label: 'hepsiJET',
      code: 'HEPSIJET',
      iconUrl: 'https://www.hepsijet.com/images/hepsijet.svg'
    })
    expect(cargoLabel(value)).toBe('hepsiJET')
  })
})

describe('cargoCarrier cost', () => {
  // Every order row asks several times per render; the aliases are normalised once, and a name already seen is answered
  // without normalising anything (each normalisation is a tr-TR upper-case and a Unicode regex).
  it('answers a name it has seen without normalising again', () => {
    const upper = vi.spyOn(String.prototype, 'toLocaleUpperCase')
    expect(cargoCarrier('Aras Kargo Marketplace')?.code).toBe('ARASMP')
    upper.mockClear()
    for (let index = 0; index < 20; index += 1) expect(cargoCarrier('Aras Kargo Marketplace')?.code).toBe('ARASMP')
    expect(upper).not.toHaveBeenCalled()
    upper.mockRestore()
  })
})
