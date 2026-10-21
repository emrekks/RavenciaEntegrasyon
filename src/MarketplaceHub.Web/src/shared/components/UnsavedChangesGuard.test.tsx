import { act, useRef, useState } from 'react'
import { afterEach, describe, expect, it } from 'vitest'
import { createMemoryRouter, Link, MemoryRouter, RouterProvider, useLocation, useNavigate } from 'react-router'
import { byText, cleanup, click, render, waitFor } from '../../test/render'
import { UnsavedChangesGuard } from './UnsavedChangesGuard'

afterEach(cleanup)

// A cancelable click, as a browser sends it: the router's link handler prevents the document navigation.
function follow(link: HTMLElement) {
  act(() => { link.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, button: 0 })) })
}

function Editor() {
  const [dirty, setDirty] = useState(false)
  const leaving = useRef(false)
  const navigate = useNavigate()
  return <>
    <UnsavedChangesGuard when={dirty} allowNavigationRef={leaving} />
    <button type="button" onClick={() => setDirty(true)}>Değiştir</button>
    <button type="button" onClick={() => { leaving.current = true; navigate('/products/42') }}>Kaydet</button>
    <Link to="/orders">Siparişler</Link>
    <Link to="/products/new?step=2">Adım 2</Link>
  </>
}
function Where() { return <output>{useLocation().pathname}</output> }

function renderInDataRouter() {
  const router = createMemoryRouter([{ path: '/products/new', element: <><Editor /><Where /></> }, { path: '*', element: <Where /> }], { initialEntries: ['/products/new'] })
  render(<RouterProvider router={router} />)
  return router
}

describe('unsaved changes guard', () => {
  it('lets navigation through while the form is clean', async () => {
    const router = renderInDataRouter()
    follow(byText(document.body, 'a', 'Siparişler'))
    await waitFor(() => router.state.location.pathname === '/orders')
  })

  it('asks before leaving a dirty form and stays when the person chooses to', async () => {
    const router = renderInDataRouter()
    click(byText(document.body, 'button', 'Değiştir'))
    follow(byText(document.body, 'a', 'Siparişler'))
    await waitFor(() => byText(document.body, 'button', 'Sayfada Kal'))
    expect(router.state.location.pathname).toBe('/products/new')
    click(byText(document.body, 'button', 'Sayfada Kal'))
    await waitFor(() => !document.body.textContent?.includes('Değişiklikleri Bırak'))
    expect(router.state.location.pathname).toBe('/products/new')

    follow(byText(document.body, 'a', 'Siparişler'))
    click(await waitFor(() => byText(document.body, 'button', 'Değişiklikleri Bırak')))
    await waitFor(() => router.state.location.pathname === '/orders')
  })

  it('does not ask for a change on the same path or for the screen’s own navigation after a save', async () => {
    const router = renderInDataRouter()
    click(byText(document.body, 'button', 'Değiştir'))
    follow(byText(document.body, 'a', 'Adım 2'))
    await waitFor(() => router.state.location.search === '?step=2')
    expect(document.body.textContent).not.toContain('Sayfada Kal')
    click(byText(document.body, 'button', 'Kaydet'))
    await waitFor(() => router.state.location.pathname === '/products/42')
    await act(async () => { await Promise.resolve() })
    expect(document.body.textContent).not.toContain('Sayfada Kal')
  })

  it('renders nothing outside a data router', () => {
    const { host } = render(<MemoryRouter><UnsavedChangesGuard when /></MemoryRouter>)
    expect(host.innerHTML).toBe('')
  })
})
