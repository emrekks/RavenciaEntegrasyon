import { describe, expect, it } from 'vitest'
import { directMappingsKeepingTargets } from '../../attribute-value-mapping'
import { fieldChoiceFailureMessage, fieldChoiceSteps } from './field-choice'
import { attributeFieldProgress } from './field-progress'

// Scan 25 Nov 2026 (D-4): the mapping windows deleted an old mapping before the new one was saved, read an unknown
// value progress as ready, and Birebir Eşle gave a panel value a second marketplace value.
describe('field choice writes', () => {
  it('saves the new panel attribute before it removes the old mapping', () => {
    expect(fieldChoiceSteps({ hasMapping: true, hasFieldSource: false, chosenSource: null, chosenLocalId: 'renk' })).toEqual(['put-mapping', 'delete-mapping'])
  })

  it('saves the product field before it removes the panel attribute it replaces', () => {
    expect(fieldChoiceSteps({ hasMapping: true, hasFieldSource: false, chosenSource: 'MODEL_CODE', chosenLocalId: '__product:MODEL_CODE' })).toEqual(['put-default', 'delete-mapping'])
  })

  it('saves the panel attribute before it removes the product field it replaces', () => {
    expect(fieldChoiceSteps({ hasMapping: false, hasFieldSource: true, chosenSource: null, chosenLocalId: 'renk' })).toEqual(['put-mapping', 'delete-default'])
  })

  it('only removes on Kaldır', () => {
    expect(fieldChoiceSteps({ hasMapping: true, hasFieldSource: false, chosenSource: null, chosenLocalId: '' })).toEqual(['delete-mapping'])
    expect(fieldChoiceSteps({ hasMapping: false, hasFieldSource: true, chosenSource: null, chosenLocalId: '' })).toEqual(['delete-default'])
  })
})

describe('a refused field choice', () => {
  it('says both stay when the old one could not be removed after the new one was saved', () => {
    expect(fieldChoiceFailureMessage('delete-mapping', true, 'Sunucu yanıt vermedi')).toContain('Yeni eşleme kaydedildi, eskisi silinemedi')
    expect(fieldChoiceFailureMessage('delete-default', true, 'x')).toContain('ikisi birlikte duruyor')
    expect(fieldChoiceFailureMessage('put-mapping', false, 'Eşleme reddedildi')).toBe('Eşleme reddedildi')
  })
})

describe('mapped field progress', () => {
  it('is unknown, not done, while the saved value pairs are not known', () => {
    expect(attributeFieldProgress({ mapped: true, needsValues: true, activeValueIds: ['a', 'b'], savedLocalIds: null })).toEqual({ state: 'unknown', meta: 'Değerler bilinmiyor' })
    expect(attributeFieldProgress({ mapped: true, needsValues: true, activeValueIds: ['a', 'b'], savedLocalIds: undefined }).state).toBe('unknown')
  })

  it('counts the active values that have a saved pair', () => {
    expect(attributeFieldProgress({ mapped: true, needsValues: true, activeValueIds: ['a', 'b'], savedLocalIds: ['a', 'gone'] })).toEqual({ state: 'partial', meta: '1/2 değer', ratio: 0.5 })
    expect(attributeFieldProgress({ mapped: true, needsValues: true, activeValueIds: ['a', 'b'], savedLocalIds: ['a', 'b'] })).toEqual({ state: 'done', meta: '2 değer', ratio: 1 })
    expect(attributeFieldProgress({ mapped: true, needsValues: false, activeValueIds: ['a'], savedLocalIds: null })).toEqual({ state: 'done', meta: 'Eşlendi' })
    expect(attributeFieldProgress({ mapped: false, needsValues: true, activeValueIds: ['a'], savedLocalIds: null })).toEqual({ state: 'none', meta: 'Eşlenmedi' })
  })
})

describe('Birebir Eşle in the value editor', () => {
  it('never gives a panel value a second marketplace value beside the one it has', () => {
    const planned = [{ localId: 'siyah', externalId: 'x-siyah' }, { localId: 'mavi', externalId: 'x-mavi' }, { localId: 'kirmizi', externalId: 'x-kirmizi' }]
    // siyah is mapped to "Antrasit" by the user; kirmizi already sits on its same-name value (a stale save is redone)
    const selections = { 'x-antrasit': ['siyah'], 'x-kirmizi': ['kirmizi'] }
    expect(directMappingsKeepingTargets(planned, selections)).toEqual([{ localId: 'mavi', externalId: 'x-mavi' }, { localId: 'kirmizi', externalId: 'x-kirmizi' }])
  })
})
