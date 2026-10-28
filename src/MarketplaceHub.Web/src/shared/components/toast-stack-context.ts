import { createContext, useContext } from 'react'
import type { ToastInput, ToastItem } from '../toast-stack'

export type ToastStackApi = {
  /** Shows a toast below the ones on screen (or in its key's place). */
  show: (input: ToastInput) => void
  dismiss: (id: string) => void
  dismissKey: (key: string) => void
  dismissWhere: (test: (item: ToastItem) => boolean) => void
}

const noop = () => {}
/** Outside the shell (tests, sign-in) there is no column to stack in; toasts shown there are dropped. */
const detachedStack: ToastStackApi = { show: noop, dismiss: noop, dismissKey: noop, dismissWhere: noop }

export const ToastStackContext = createContext<ToastStackApi>(detachedStack)
/** The top-right column every toast of the shell is drawn in, page-owned toasts included. */
export const ToastViewportContext = createContext<HTMLElement | null>(null)

export function useToastStack() {
  return useContext(ToastStackContext)
}
