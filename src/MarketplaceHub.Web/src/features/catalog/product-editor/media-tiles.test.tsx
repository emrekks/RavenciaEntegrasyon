import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, click, flush, render } from '../../../test/render'
import { LocalImagePreview, MediaAddTile } from './media-tiles'

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

  it('does not zoom when the picture itself is clicked', async () => {
    const onZoom = vi.fn()
    const { host } = render(<LocalImagePreview file={picture} alt="Ürün 1" caption="urun.jpg" draggable onZoom={onZoom} />)
    await flush()
    const image = host.querySelector('img')!
    click(image)
    expect(onZoom).not.toHaveBeenCalled()
    expect(image.classList.contains('clickable-thumb')).toBe(false)
    expect(image.title).not.toContain('Büyütmek')
  })

  it('has no order arrows: pictures are sorted by dragging', async () => {
    const { host } = render(<LocalImagePreview file={picture} alt="Ürün 1" caption="urun.jpg" draggable mediaIndex={0} />)
    await flush()
    expect(host.querySelector('.media-move-buttons')).toBeNull()
    expect(host.querySelectorAll('button')).toHaveLength(0)
  })

  it('opens the file picker from the plus tile and takes dropped files, but not a dragged picture', () => {
    const onOpen = vi.fn()
    const onFiles = vi.fn()
    const { host } = render(<MediaAddTile hint="JPEG veya PNG" onOpen={onOpen} onFiles={onFiles} />)
    const tile = host.querySelector<HTMLButtonElement>('button.media-add-tile')!
    expect(tile.type).toBe('button')
    expect(tile.textContent).toContain('Görsel Ekle')
    expect(tile.title).toBe('JPEG veya PNG')
    expect(tile.draggable).toBe(false)
    click(tile)
    expect(onOpen).toHaveBeenCalledTimes(1)
    const drop = (types: string[], files: File[]) => {
      const event = new Event('drop', { bubbles: true, cancelable: true })
      Object.defineProperty(event, 'dataTransfer', { value: { types, files, dropEffect: 'none' } })
      tile.dispatchEvent(event)
      return event
    }
    expect(drop(['text/plain'], []).defaultPrevented).toBe(false)
    expect(onFiles).not.toHaveBeenCalled()
    expect(drop(['Files'], [picture]).defaultPrevented).toBe(true)
    expect(onFiles).toHaveBeenCalledWith([picture])
  })

  it('has no zoom button when the tile cannot zoom', async () => {
    const { host } = render(<LocalImagePreview file={picture} alt="Ürün 1" caption="urun.jpg" />)
    await flush()
    expect(host.querySelector('.image-zoom-btn')).toBeNull()
  })

})
