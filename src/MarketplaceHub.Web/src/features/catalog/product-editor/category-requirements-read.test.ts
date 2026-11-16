import { describe, expect, it } from 'vitest'
import { readCategoryRequirements } from './publication-readiness'

const attribute = { id: 'a1', code: 'renk', name: 'Renk', dataType: 'SINGLE_SELECT', selectionMode: null, unit: null, isActive: true, version: 1, values: [], roles: null }
const row = (role: string) => ({ attributeId: 'a1', isRequired: true, allowsCustomValue: false, displayOrder: 1, attribute, role, isWebColor: false })

describe('category requirements as the editor reads them', () => {
  it('keeps OPTION and ATTRIBUTE and reads an unknown role as an ordinary attribute field, never dropping it', () => {
    expect(readCategoryRequirements([row('OPTION'), row('ATTRIBUTE'), row('LATER')]).map(item => item.role)).toEqual(['OPTION', 'ATTRIBUTE', 'ATTRIBUTE'])
    expect(readCategoryRequirements([row('LATER')])[0]).toMatchObject({ isRequired: true, attributeId: 'a1' })
  })
})
