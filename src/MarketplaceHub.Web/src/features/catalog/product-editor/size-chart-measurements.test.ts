import { describe, expect, it } from 'vitest'
import { addSizeChartColumn, initialSizeChartDraft, isSizeOptionLabel, newColumnIssue, parseSizeChartValue, removeSizeChartColumn, setSizeChartCell, validateSizeChartDraft } from './size-chart-measurements'

describe('size chart measurements', () => {
  it('accepts comma or dot with at most one decimal', () => {
    expect(parseSizeChartValue('64')).toBe(64)
    expect(parseSizeChartValue('64,5')).toBe(64.5)
    expect(parseSizeChartValue(' 64.5 ')).toBe(64.5)
    expect(parseSizeChartValue('64,55')).toBe('En fazla bir ondalık basamak girin.')
    expect(parseSizeChartValue('abc')).toBe('Yalnız sayı girin.')
    expect(parseSizeChartValue('')).toBe('Ölçü girin.')
    expect(parseSizeChartValue('0')).toBe("Ölçü 0'dan büyük olmalı.")
    expect(parseSizeChartValue('501')).toBe('Ölçü en fazla 500 cm olabilir.')
    expect(parseSizeChartValue('-3')).toBe('Yalnız sayı girin.')
  })

  it('opens with Boy and En and the sizes in panel order, prefilled from the saved numbers', () => {
    const draft = initialSizeChartDraft(['S', 'M', 'L'], { columns: ['Boy', 'En', 'Göğüs'], rows: [{ size: 'm', values: [66, 53.5, null] }, { size: 'XXL', values: [1, 2, 3] }] })
    expect(draft.columns).toEqual(['Boy', 'En', 'Göğüs'])
    expect(draft.rows.map(row => row.size)).toEqual(['S', 'M', 'L'])
    expect(draft.rows[1].values).toEqual(['66', '53,5', ''])
    expect(initialSizeChartDraft(['S'], null).columns).toEqual(['Boy', 'En'])
  })

  it('reports every empty or wrong cell and sends the exact numbers', () => {
    let draft = initialSizeChartDraft(['S', 'M'], null)
    const failed = validateSizeChartDraft(draft)
    expect(failed.ok).toBe(false)
    if (!failed.ok) expect(Object.keys(failed.cellErrors)).toEqual(['0:0', '0:1', '1:0', '1:1'])
    draft = setSizeChartCell(setSizeChartCell(setSizeChartCell(setSizeChartCell(draft, 0, 0, '64'), 0, 1, '50,5'), 1, 0, '66'), 1, 1, '53')
    expect(validateSizeChartDraft(draft)).toEqual({ ok: true, payload: { columns: ['Boy', 'En'], rows: [{ size: 'S', values: [64, 50.5] }, { size: 'M', values: [66, 53] }] } })
  })

  it('checks column names: not empty, unique, short, at most eight', () => {
    expect(newColumnIssue('', ['Boy'])).toBe('Kolon adı girin.')
    expect(newColumnIssue('boy', ['Boy'])).toBe('Bu kolon zaten var.')
    expect(newColumnIssue('x'.repeat(25), ['Boy'])).toBe('Kolon adı en fazla 24 karakter olabilir.')
    expect(newColumnIssue('Bel', ['1', '2', '3', '4', '5', '6', '7', '8'])).toBe('En fazla 8 kolon eklenebilir.')
    expect(newColumnIssue('Kol Boyu', ['Boy', 'En'])).toBeNull()
    const draft = { columns: ['Boy', ' boy '], rows: [{ size: 'S', values: ['1', '2'] }] }
    const result = validateSizeChartDraft(draft)
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.columnErrors).toEqual({ 1: 'Bu kolon zaten var.' })
  })

  it('adds and removes columns but keeps at least one', () => {
    const draft = addSizeChartColumn(initialSizeChartDraft(['S'], null), '  Kol   Boyu ')
    expect(draft.columns).toEqual(['Boy', 'En', 'Kol Boyu'])
    expect(draft.rows[0].values).toHaveLength(3)
    const one = removeSizeChartColumn(removeSizeChartColumn(draft, 0), 0)
    expect(one.columns).toEqual(['Kol Boyu'])
    expect(removeSizeChartColumn(one, 0)).toBe(one)
  })

  it('recognises the Beden option in any case', () => {
    expect(isSizeOptionLabel('BEDEN')).toBe(true)
    expect(isSizeOptionLabel(' beden ')).toBe(true)
    expect(isSizeOptionLabel('Renk')).toBe(false)
  })
})
