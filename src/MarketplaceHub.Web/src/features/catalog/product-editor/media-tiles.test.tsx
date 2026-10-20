import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, click, flush, render } from '../../../test/render'
import { LocalImagePreview, MediaMoveButtons } from './media-tiles'

beforeEach(() => {
  Object.assign(URL, { createObjectURL: vi.fn(() => 'blob:picture-1'), revokeObjectURL: vi.fn() })
})
afterEach(() => cleanup())

const picture = new File(['x'], 'urun.jpg', { type: 'image/jpeg' })

describe('product picture tile', () => {
  it('opens the zoom from a keyboard-reachable button and keeps the tile itself the drag handle', async () => {
    const onZoom = vi.fn()
    const onTileClick = vi.fn()
    const { host } = render(<div onClick={onTileClick} role="presentation"><LocalImagePreview file={picture} alt="Ürün 1" caption="urun.jpg" draggable onZoom={onZoom} /></div>)
    await flush()
    const tile = host.querySelector('figure')!
    const zoom = host.querySelector<HTMLButtonElement>('button.image-zoom-btn')!
    // A real button in the Tab order (Enter and Space press it), named for screen readers.
    expect(zoom.type).toBe('button')
    expect(zoom.tabIndex).toBe(0)
    expect(zoom.getAttribute('aria-label')).toBe('Görseli büyüt')
    // The drag stays on the tile: the button is a small corner control next to the picture, not a wrapper around it.
    expect(tile.draggable).toBe(true)
    expect(zoom.contains(host.querySelector('img'))).toBe(false)
    zoom.focus()
    expect(document.activeElement).toBe(zoom)
    click(zoom)
    expect(onZoom).toHaveBeenCalledWith('blob:picture-1')
    expect(onTileClick).not.toHaveBeenCalled()
  })

  it('has no zoom button when the tile cannot zoom', async () => {
    const { host } = render(<LocalImagePreview file={picture} alt="Ürün 1" caption="urun.jpg" />)
    await flush()
    expect(host.querySelector('.image-zoom-btn')).toBeNull()
  })

  it('moves a picture one place with the order buttons', () => {
    const onMove = vi.fn()
    const { host } = render(<MediaMoveButtons index={1} count={3} onMove={onMove} />)
    const [earlier, later] = host.querySelectorAll('button')
    click(earlier)
    click(later)
    expect(onMove.mock.calls).toEqual([[0], [2]])
  })
})
