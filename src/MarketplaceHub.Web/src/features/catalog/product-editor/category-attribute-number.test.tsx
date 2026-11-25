import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { AttributeTextValueInput } from './AttributeTextValueInput'
import { CategoryAttributeMappingPanel } from './CategoryAttributeMappingPanel'
import { productAttributePayload } from './editor-helpers'
import { attributeNumberUnreadable, savedAttributeText } from './editor-number'
import type { CategoryRequirement } from './publication-readiness'

// D-1 (scan 25 Nov 2026): a numeric category attribute was a type="number" box; a tr-TR browser refused "12,5" and gave
// "1,125" as 1125, and a stored 1.5 opened as "1.5", which the save then refused.
const requirement = { attributeId: 'a-en', isRequired: false, allowsCustomValue: true, displayOrder: 1, role: 'ATTRIBUTE', isWebColor: false, attribute: { id: 'a-en', version: 1, code: 'EN', name: 'Kumaş Eni', dataType: 'NUMBER', values: [] } } as unknown as CategoryRequirement

describe('numeric category attribute', () => {
  it('sends Turkish text as the number it shows', () => {
    expect(productAttributePayload(requirement, [], '12,5', 0)).toEqual([{ attributeId: 'a-en', valueId: null, textValue: null, numberValue: 12.5, booleanValue: null, sortOrder: 0 }])
    expect(productAttributePayload(requirement, [], '1,125', 0)[0].numberValue).toBe(1.125)
    expect(productAttributePayload(requirement, [], '1.250', 0)[0].numberValue).toBe(1250)
  })
  it('refuses a point used as a decimal point with the way to write it', () => {
    expect(() => productAttributePayload(requirement, [], '12.5', 0)).toThrow('Kumaş Eni sayısal olmalıdır; 12,5 biçiminde yazın (ondalık için virgül, nokta yalnız binlik ayırıcıdır).')
    expect(attributeNumberUnreadable('12.5')).toBe(true)
    expect(attributeNumberUnreadable('12,5')).toBe(false)
    expect(attributeNumberUnreadable('')).toBe(false)
  })
  it('opens a stored number as Turkish text that saves back unchanged', () => {
    expect(savedAttributeText({ numberValue: 1.5 })).toBe('1,5')
    expect(savedAttributeText({ numberValue: 0.125 })).toBe('0,125')
    for (const stored of [1.5, 0.125, 1250, 12]) expect(productAttributePayload(requirement, [], savedAttributeText({ numberValue: stored })!, 0)[0].numberValue).toBe(stored)
    expect(savedAttributeText({ textValue: 'Pamuk' })).toBe('Pamuk')
    expect(savedAttributeText({ booleanValue: false })).toBe('hayır')
    expect(savedAttributeText({})).toBeNull()
  })
  it('renders a Turkish text box, marked when the text cannot be read', () => {
    const render = (typed: string) => renderToStaticMarkup(<CategoryAttributeMappingPanel categoryId="c1" categoryLabel="Elbise" requirements={[requirement]} isLoading={false} isError={false} attributeSelections={{}} attributeTextValues={{ 'a-en': typed }} onToggleValue={() => undefined} onTextChange={() => undefined} webColorAutoEnabled manualWebColorValueId="" onToggleWebColorAuto={() => undefined} onManualWebColorValueChange={() => undefined} />)
    const box = (html: string) => /<input[^>]*aria-label="Kumaş Eni değeri"[^>]*>/.exec(html)?.[0] ?? ''
    expect(box(render('12,5'))).toContain('type="text"')
    expect(box(render('12,5'))).toContain('inputMode="decimal"')
    expect(box(render('12,5'))).not.toContain('aria-invalid')
    expect(box(render('12.5'))).toContain('aria-invalid="true"')
  })
  // Review of D-1: Seçenekler ve Özellikler's own value box (EditorOptionsSection) was still type="number".
  it('gives every typed attribute value box of the editor the same Turkish text box', () => {
    const numeric = renderToStaticMarkup(<AttributeTextValueInput numeric value="12.5" onChange={() => undefined} placeholder="Değer girin" />)
    expect(numeric).toContain('type="text"')
    expect(numeric).toContain('inputMode="decimal"')
    expect(numeric).toContain('aria-invalid="true"')
    expect(renderToStaticMarkup(<AttributeTextValueInput numeric={false} value="Pamuk" onChange={() => undefined} placeholder="Değer girin" />)).not.toContain('inputMode')
    const sources = import.meta.glob('./*.tsx', { query: '?raw', import: 'default', eager: true }) as Record<string, string>
    expect(sources['./EditorOptionsSection.tsx']).toContain(`<AttributeTextValueInput numeric={item.attribute.dataType === 'NUMBER'}`)
    for (const file of ['./EditorOptionsSection.tsx', './CategoryAttributeMappingPanel.tsx']) expect(sources[file]).not.toMatch(/type=\{[^}]*'number'|type="number"/)
  })
})
