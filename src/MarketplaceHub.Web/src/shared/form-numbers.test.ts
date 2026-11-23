import { describe, expect, it } from 'vitest'
import { readFormCount, readFormMeasure } from './form-numbers'

describe('form number readers (shipment label, Hepsiburada package, return quantity)', () => {
  it('reads Turkish counts within range and refuses the rest', () => {
    expect(readFormCount('1.000', 'Adet', { min: 1 })).toEqual({ ok: true, value: 1000 })
    expect(readFormCount('2', 'Koli Adedi', { min: 1, max: 50 })).toEqual({ ok: true, value: 2 })
    for (const text of ['1e3', '0x10', '2,5', '1,000', '-1', '', 'abc']) expect(readFormCount(text, 'Adet', { min: 1 }).ok, text).toBe(false)
    expect(readFormCount('51', 'Koli Adedi', { min: 1, max: 50 })).toEqual({ ok: false, message: 'Koli Adedi en çok 50 olabilir.' })
    expect(readFormCount('0', 'Adet', { min: 1 })).toEqual({ ok: false, message: 'Adet en az 1 olmalıdır.' })
  })
  it('reads Turkish measurements and refuses a point used as a decimal point', () => {
    expect(readFormMeasure('1.250', 'Desi', { min: 0, max: 10000 })).toEqual({ ok: true, value: 1250 })
    expect(readFormMeasure('0,5', 'Desi', { min: 0, max: 10000 })).toEqual({ ok: true, value: 0.5 })
    expect(readFormMeasure('1.25', 'Desi', { min: 0, max: 10000 }).ok).toBe(false)
    expect(readFormMeasure('1e3', 'Desi', { min: 0, max: 10000 }).ok).toBe(false)
    expect(readFormMeasure('0', 'Desi/Hacim', { above: 0, max: 10000 })).toEqual({ ok: false, message: "Desi/Hacim 0'dan büyük olmalıdır." })
    expect(readFormMeasure('12.000', 'Desi', { min: 0, max: 10000 })).toEqual({ ok: false, message: 'Desi en çok 10.000 olabilir.' })
  })
})
