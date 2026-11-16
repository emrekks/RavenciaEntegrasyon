import { describe, expect, it } from 'vitest'
import { readChoiceEnvelope } from './use-platform-update-choice'

describe('the stored Hızlı Güncelle choice as the panel reads it', () => {
  it('reads a choice record and treats any other stored object as no choice', () => {
    const record = { fields: ['PRICE'], openGroups: ['sales'], knownFields: ['PRICE'], connectionIds: null }
    expect(readChoiceEnvelope({ settings: record, version: 3 })).toEqual({ version: 3, settings: { fields: ['PRICE'], openGroups: ['sales'], knownFields: ['PRICE'], connectionIds: null } })
    expect(readChoiceEnvelope({ settings: { ...record, connectionIds: ['c1'] }, version: 4 }).settings?.connectionIds).toEqual(['c1'])
    expect(readChoiceEnvelope({ settings: { fields: 'PRICE', openGroups: [] }, version: 5 })).toEqual({ version: 5, settings: null })
    expect(readChoiceEnvelope({ settings: null, version: 0 })).toEqual({ version: 0, settings: null })
  })
})
