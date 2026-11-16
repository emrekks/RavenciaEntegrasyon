import { act } from 'react'
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render } from '../../test/render'
import { useServerDraft, type ServerCopy, type ServerDraft, type ServerDraftPolicy } from './useServerDraft'

type Form = { name: string }
let hook: ServerDraft<Form, Form>
let renders = 0

function Probe({ server, policy, isDirty }: { server: ServerCopy<Form> | null; policy: ServerDraftPolicy; isDirty?: (draft: Form, saved: Form) => boolean }) {
  renders++
  hook = useServerDraft<Form>({ server, onServerChange: policy, toDraft: value => ({ ...value }), initialDraft: () => ({ name: '' }), isDirty })
  return <output>{hook.draft.name}</output>
}

const copy = (name: string, version: number): ServerCopy<Form> => ({ value: { name }, version })

afterEach(cleanup)

describe('useServerDraft keep-edits (WEB-SAYFALAR-4)', () => {
  it('starts from the copy read, or from the initial form while nothing is read', () => {
    const { rerender, host } = render(<Probe server={null} policy="keep-edits" />)
    expect(hook.draft.name).toBe('')
    expect(hook.dirty).toBe(false)
    expect(hook.base).toBeNull()
    rerender(<Probe server={copy('A', 1)} policy="keep-edits" />)
    expect(hook.draft.name).toBe('A')
    expect(hook.base).toEqual(copy('A', 1))
    expect(host.textContent).toBe('A')
  })

  it('a clean draft follows a newer copy; an edited one stays and the newer copy waits as a conflict', () => {
    const { rerender } = render(<Probe server={copy('A', 1)} policy="keep-edits" />)
    rerender(<Probe server={copy('B', 2)} policy="keep-edits" />)
    expect(hook.draft.name).toBe('B')
    expect(hook.conflict).toBe(false)
    act(() => hook.setDraft({ name: 'Mine' }))
    expect(hook.dirty).toBe(true)
    rerender(<Probe server={copy('C', 3)} policy="keep-edits" />)
    expect(hook.draft.name).toBe('Mine')
    expect(hook.base?.version).toBe(2)
    expect(hook.conflict).toBe(true)
  })

  it('a newer version of the same settings only moves the version', () => {
    const { rerender } = render(<Probe server={copy('A', 1)} policy="keep-edits" />)
    act(() => hook.setDraft(current => ({ name: `${current.name}!` })))
    rerender(<Probe server={copy('A', 2)} policy="keep-edits" />)
    expect(hook.draft.name).toBe('A!')
    expect(hook.base).toEqual(copy('A', 2))
    expect(hook.conflict).toBe(false)
  })

  it('acceptServer drops the draft for the newer copy; commit makes the saved copy the base', () => {
    const { rerender } = render(<Probe server={copy('A', 1)} policy="keep-edits" />)
    act(() => hook.setDraft({ name: 'Mine' }))
    rerender(<Probe server={copy('C', 2)} policy="keep-edits" />)
    act(() => hook.acceptServer())
    expect(hook.draft.name).toBe('C')
    expect(hook.conflict).toBe(false)
    act(() => hook.setDraft({ name: 'Mine' }))
    act(() => hook.commit(copy('Mine', 3)))
    expect(hook.base).toEqual(copy('Mine', 3))
    expect(hook.draft.name).toBe('Mine')
    rerender(<Probe server={copy('Mine', 3)} policy="keep-edits" />)
    expect(hook.dirty).toBe(false)
    expect(hook.conflict).toBe(false)
  })

  it('a refetch of the same copy does not render again and again', () => {
    const { rerender } = render(<Probe server={copy('A', 1)} policy="keep-edits" />)
    renders = 0
    rerender(<Probe server={copy('A', 1)} policy="keep-edits" />)
    expect(renders).toBe(1)
  })
})

describe('useServerDraft replace', () => {
  it('keeps an edit across a refetch of the same version and drops it for a new version, never a conflict', () => {
    const { rerender } = render(<Probe server={copy('A', 1)} policy="replace" />)
    act(() => hook.setDraft({ name: 'Mine' }))
    rerender(<Probe server={copy('A changed status', 1)} policy="replace" />)
    expect(hook.draft.name).toBe('Mine')
    rerender(<Probe server={copy('B', 2)} policy="replace" />)
    expect(hook.draft.name).toBe('B')
    expect(hook.conflict).toBe(false)
  })

  it('counts a change by the rule given', () => {
    render(<Probe server={copy('A', 1)} policy="replace" isDirty={(draft, saved) => draft.name.trim().toLowerCase() !== saved.name.toLowerCase()} />)
    act(() => hook.setDraft({ name: ' a ' }))
    expect(hook.dirty).toBe(false)
    act(() => hook.setDraft({ name: 'b' }))
    expect(hook.dirty).toBe(true)
  })
})
