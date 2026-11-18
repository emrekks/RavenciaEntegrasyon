import { afterEach, describe, expect, it } from 'vitest'
import { blurNumberInputOnWheel } from './number-wheel'

afterEach(() => { document.body.innerHTML = '' })

describe('blurNumberInputOnWheel', () => {
  it('takes focus off a focused number box and never cancels the wheel, so the page still scrolls', () => {
    const input = document.createElement('input')
    input.type = 'number'
    input.value = '5'
    document.body.append(input)
    input.focus()
    expect(document.activeElement).toBe(input)
    const event = new WheelEvent('wheel', { deltaY: 120, bubbles: true, cancelable: true })
    input.addEventListener('wheel', blurNumberInputOnWheel as EventListener)
    input.dispatchEvent(event)
    expect(document.activeElement).not.toBe(input)
    expect(input.value).toBe('5')
    expect(event.defaultPrevented).toBe(false)
  })

  it('leaves text boxes and unfocused number boxes alone', () => {
    const text = document.createElement('input')
    document.body.append(text)
    text.focus()
    text.addEventListener('wheel', blurNumberInputOnWheel as EventListener)
    const event = new WheelEvent('wheel', { deltaY: 120, bubbles: true, cancelable: true })
    text.dispatchEvent(event)
    expect(document.activeElement).toBe(text)
    expect(event.defaultPrevented).toBe(false)
  })
})
