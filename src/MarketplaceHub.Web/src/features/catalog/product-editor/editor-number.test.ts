import { describe, expect, it } from 'vitest'
import { editorCount, parseEditorNumber } from './editor-number'

describe('parseEditorNumber (measurements, VAT, attributes)', () => {
  it('reads empty as 0 and a number as it is', () => {
    expect(parseEditorNumber('')).toBe(0)
    expect(parseEditorNumber('  ')).toBe(0)
    expect(parseEditorNumber(12.5)).toBe(12.5)
  })
  // B1: every editor box is a Turkish text box; a point only groups thousands.
  it('reads typed Turkish text: a decimal comma and thousands points', () => {
    expect(parseEditorNumber('549,90')).toBe(549.9)
    expect(parseEditorNumber('1.250')).toBe(1250)
    expect(parseEditorNumber('1.249,90')).toBe(1249.9)
    expect(parseEditorNumber('1 249,90')).toBe(1249.9)
    expect(parseEditorNumber('0,125')).toBe(0.125)
    expect(parseEditorNumber('-3')).toBe(-3)
  })
  it('gives NaN for a point used as a decimal point and for text that is not a number', () => {
    for (const text of ['549.9', '1.25', '.5', '1e3', '0x10', 'abc', '1,2,3']) expect(parseEditorNumber(text), text).toBeNaN()
  })
})

describe('editorCount (stock, safety stock)', () => {
  it('reads Turkish whole numbers and refuses anything else', () => {
    expect(editorCount('')).toBe(0)
    expect(editorCount('1.000')).toBe(1000)
    expect(editorCount('12')).toBe(12)
    for (const text of ['2,5', '1e3', '0x10', '-1', '1,000', '1.5']) expect(editorCount(text), text).toBeNaN()
  })
})
