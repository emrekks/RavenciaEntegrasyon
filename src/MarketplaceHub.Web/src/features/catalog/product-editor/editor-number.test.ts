import { describe, expect, it } from 'vitest'
import { parseEditorNumber } from './editor-number'

describe('parseEditorNumber', () => {
  it('reads empty as 0 and a number as it is', () => {
    expect(parseEditorNumber('')).toBe(0)
    expect(parseEditorNumber('  ')).toBe(0)
    expect(parseEditorNumber(12.5)).toBe(12.5)
  })
  it('reads what a number field gives as JavaScript does: a single point is a decimal point', () => {
    expect(parseEditorNumber('549.9')).toBe(549.9)
    expect(parseEditorNumber('1.250')).toBe(1.25)
    expect(parseEditorNumber('-3')).toBe(-3)
    expect(parseEditorNumber('.5')).toBe(0.5)
    expect(parseEditorNumber('5.')).toBe(5)
  })
  it('reads typed Turkish text: a decimal comma and thousands points', () => {
    expect(parseEditorNumber('549,90')).toBe(549.9)
    expect(parseEditorNumber('1.249,90')).toBe(1249.9)
    expect(parseEditorNumber('1 249,90')).toBe(1249.9)
    expect(parseEditorNumber('549,')).toBe(549)
  })
  it('gives NaN for text that is not a number', () => {
    expect(parseEditorNumber('abc')).toBeNaN()
    expect(parseEditorNumber('1,2,3')).toBeNaN()
  })
})
