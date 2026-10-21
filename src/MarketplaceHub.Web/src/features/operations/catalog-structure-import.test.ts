import { describe, expect, it } from 'vitest'
import { catalogStructureImportChange } from './catalog-structure-import'

describe('catalog structure job change', () => {
  it('states that the job never writes to the marketplace', () => {
    expect(catalogStructureImportChange.detail).toContain('pazaryerine veri gönderilmedi')
  })
})
