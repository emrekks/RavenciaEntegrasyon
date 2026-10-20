import { useCallback, useId, useRef, useState, type FormEvent, type ReactNode } from 'react'
import { Button, Modal } from './DesignSystem'

// The panel's own confirmation and short-answer windows, in place of the browser's confirm() and prompt(): they follow
// the theme, trap focus, close on Escape and can say plainly what will happen.

export type ConfirmOptions = {
  title: string
  message: ReactNode
  confirmLabel?: string
  cancelLabel?: string
  /** The action cannot be undone or removes something: the confirm button is red. */
  destructive?: boolean
}

type Pending<T, R> = { options: T; resolve: (value: R) => void }

/** `const [confirm, confirmDialog] = useConfirmDialog()`; render `confirmDialog`, then `if (!(await confirm({...}))) return`. */
export function useConfirmDialog(): [(options: ConfirmOptions) => Promise<boolean>, ReactNode] {
  const [pending, setPending] = useState<Pending<ConfirmOptions, boolean> | null>(null)
  const confirm = useCallback((options: ConfirmOptions) => new Promise<boolean>(resolve => setPending({ options, resolve })), [])
  const settle = (value: boolean) => { pending?.resolve(value); setPending(null) }
  const element = pending ? <Modal open title={pending.options.title} onClose={() => settle(false)} className="rv-confirm-dialog" footer={<>
    <Button variant="secondary" onClick={() => settle(false)} data-autofocus={pending.options.destructive ? true : undefined}>{pending.options.cancelLabel ?? 'Vazgeç'}</Button>
    <Button variant={pending.options.destructive ? 'danger' : 'primary'} onClick={() => settle(true)} data-autofocus={pending.options.destructive ? undefined : true}>{pending.options.confirmLabel ?? 'Devam Et'}</Button>
  </>}><div className="rv-confirm-message">{pending.options.message}</div></Modal> : null
  return [confirm, element]
}

export type PromptOptions = {
  title: string
  label: string
  initialValue?: string
  placeholder?: string
  confirmLabel?: string
  inputType?: 'text' | 'url'
  /** A message when the value cannot be used; null when it can. */
  validate?: (value: string) => string | null
}

function PromptForm({ options, onDone }: { options: PromptOptions; onDone: (value: string | null) => void }) {
  const [value, setValue] = useState(options.initialValue ?? '')
  const [problem, setProblem] = useState<string | null>(null)
  const inputId = useId()
  const problemId = useId()
  const formRef = useRef<HTMLFormElement>(null)
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const found = options.validate?.(value) ?? null
    setProblem(found)
    if (!found) onDone(value.trim())
  }
  return <Modal open title={options.title} onClose={() => onDone(null)} className="rv-confirm-dialog" footer={<>
    <Button variant="secondary" onClick={() => onDone(null)}>Vazgeç</Button>
    <Button onClick={() => formRef.current?.requestSubmit()}>{options.confirmLabel ?? 'Tamam'}</Button>
  </>}>
    <form ref={formRef} className="rv-prompt-form" onSubmit={submit} noValidate>
      <label className="rv-field" htmlFor={inputId}><span>{options.label}</span>
        <input id={inputId} type={options.inputType ?? 'text'} value={value} placeholder={options.placeholder} onChange={event => { setValue(event.target.value); if (problem) setProblem(null) }} aria-invalid={Boolean(problem)} aria-describedby={problem ? problemId : undefined} data-autofocus />
      </label>
      {problem && <small id={problemId} className="rv-field-error" role="alert">{problem}</small>}
    </form>
  </Modal>
}

/** `const [prompt, promptDialog] = usePromptDialog()`; `const value = await prompt({...})` is null when cancelled. */
export function usePromptDialog(): [(options: PromptOptions) => Promise<string | null>, ReactNode] {
  const [pending, setPending] = useState<Pending<PromptOptions, string | null> | null>(null)
  const prompt = useCallback((options: PromptOptions) => new Promise<string | null>(resolve => setPending({ options, resolve })), [])
  const element = pending ? <PromptForm options={pending.options} onDone={value => { pending.resolve(value); setPending(null) }} /> : null
  return [prompt, element]
}
