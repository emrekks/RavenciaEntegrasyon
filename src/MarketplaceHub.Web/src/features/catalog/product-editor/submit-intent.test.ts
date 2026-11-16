import { describe, expect, it } from 'vitest'
import { readSubmitIntent } from './submit-intent'

const button = (attributes: Record<string, string>) => {
  const element = document.createElement('button')
  for (const [name, value] of Object.entries(attributes)) element.setAttribute(name, value)
  return element
}

describe('readSubmitIntent', () => {
  it('reads the button’s data-submit-intent, its intent value, or the form’s intent field', () => {
    expect(readSubmitIntent(button({ 'data-submit-intent': 'draft' }), null)).toEqual({ draft: true, save: false, create: false })
    expect(readSubmitIntent(button({ 'data-submit-intent': 'save' }), null)).toEqual({ draft: false, save: true, create: false })
    expect(readSubmitIntent(button({ name: 'intent', value: 'save' }), null)).toEqual({ draft: false, save: true, create: false })
    expect(readSubmitIntent(button({ name: 'intent', value: 'create' }), null)).toEqual({ draft: false, save: false, create: true })
    expect(readSubmitIntent(null, 'save')).toEqual({ draft: false, save: true, create: false })
    expect(readSubmitIntent(undefined, null)).toEqual({ draft: false, save: false, create: false })
  })
})
