import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render } from '../../test/render'
import { profileInitials, UserAvatar } from './UserAvatar'

afterEach(cleanup)

describe('profileInitials', () => {
  it('takes the first letters of the first and the last word, at most two', () => {
    expect(profileInitials('Ayşe')).toBe('A')
    expect(profileInitials('Ayşe Yılmaz')).toBe('AY')
    expect(profileInitials('  mehmet ali   kaya ')).toBe('MK')
  })
  it('upper-cases Turkish letters the Turkish way and gives nothing for an empty name', () => {
    expect(profileInitials('ilker ışık')).toBe('İI')
    expect(profileInitials('şule çelik')).toBe('ŞÇ')
    expect(profileInitials('   ')).toBe('')
  })
})

describe('UserAvatar', () => {
  it('shows the initials, the fallback for an empty name, or the picture (initials again when it does not load)', () => {
    expect(render(<UserAvatar name="Ayşe Yılmaz" className="a" />).host.querySelector('.a')?.textContent).toBe('AY')
    cleanup()
    expect(render(<UserAvatar name="" fallback="RA" className="a" />).host.querySelector('.a')?.textContent).toBe('RA')
    cleanup()
    const { host } = render(<UserAvatar name="Ayşe Yılmaz" imageUrl="/p.png" className="a" />)
    const image = host.querySelector('img')!
    expect(image.getAttribute('src')).toBe('/p.png')
  })
})
