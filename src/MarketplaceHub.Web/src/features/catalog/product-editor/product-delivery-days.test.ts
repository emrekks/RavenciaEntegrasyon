import { describe, expect, it } from 'vitest'
import { defaultProductDeliveryDays, isProductDeliveryDays, productDeliveryDays } from './product-delivery-days'

describe('product delivery days', () => {
  it('takes whole days from 1 to 30', () => {
    expect(['1', ' 3 ', '30'].map(isProductDeliveryDays)).toEqual([true, true, true])
    expect(['', '0', '31', '1.5', '-1', 'iki'].map(isProductDeliveryDays)).toEqual([false, false, false, false, false, false])
  })

  it('sends the typed days, else the default of one day', () => {
    expect(defaultProductDeliveryDays).toBe(1)
    expect(productDeliveryDays('4')).toBe(4)
    expect(productDeliveryDays('')).toBe(1)
  })
})
