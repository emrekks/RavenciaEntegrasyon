import { describe, expect, it } from 'vitest'
import { cargoCarrier, cargoLabel } from './CargoProviderIcon'

describe('hepsiJET carrier identity', () => {
  it.each(['hepsiJET', 'HEPSIJET', 'HEPSIJETMP', 'HepsiJet Kargo'])('%s resolves to the shared carrier logo', value => {
    expect(cargoCarrier(value)).toMatchObject({
      label: 'hepsiJET',
      code: 'HEPSIJET',
      iconUrl: 'https://www.hepsijet.com/images/hepsijet.svg'
    })
    expect(cargoLabel(value)).toBe('hepsiJET')
  })
})
