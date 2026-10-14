import { describe, expect, it } from 'vitest'
import { catalogStructureImportChange, catalogStructureImportPresentation } from './catalog-structure-import'

describe('catalog structure job presentation', () => {
  it('states that the job never writes to the marketplace', () => {
    expect(catalogStructureImportPresentation.description).toContain('Pazaryerine veri gönderilmez')
    expect(catalogStructureImportChange.detail).toContain('pazaryerine veri gönderilmedi')
  })
})
