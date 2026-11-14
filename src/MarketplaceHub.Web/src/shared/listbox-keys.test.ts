import { describe, expect, it } from 'vitest'
import { moveListboxFocus } from './listbox-keys'

function setup() {
  const menu = document.createElement('div')
  menu.innerHTML = '<input /><div><button role="option">A</button><button role="option">B</button><button role="option">C</button></div>'
  document.body.appendChild(menu)
  const press = (key: string) => { let prevented = false; moveListboxFocus({ key, currentTarget: menu, preventDefault: () => { prevented = true } }); return prevented }
  return { menu, press, options: Array.from(menu.querySelectorAll<HTMLElement>('[role="option"]')), input: menu.querySelector('input')! }
}

describe('moveListboxFocus', () => {
  it('walks down from the search box, stops at the ends and returns to the search box', () => {
    const { press, options, input } = setup()
    input.focus()
    ;(Element.prototype as { scrollIntoView?: () => void }).scrollIntoView ??= () => {}
    expect(press('ArrowDown')).toBe(true)
    expect(document.activeElement).toBe(options[0])
    press('ArrowDown'); press('ArrowDown'); press('ArrowDown')
    expect(document.activeElement).toBe(options[2])
    press('ArrowUp'); press('ArrowUp')
    expect(document.activeElement).toBe(options[0])
    press('ArrowUp')
    expect(document.activeElement).toBe(input)
  })
  it('ignores other keys', () => {
    const { press } = setup()
    expect(press('a')).toBe(false)
  })
})
