// The variant table's two code column menus close on an outside click; the code change confirmation on Escape.
import { useEffect } from 'react'
import { insideSelector, useDismissable } from '../../../shared/hooks/useDismissable'
import type { EditorProps } from './editor-types'
import type { EditorStateResult } from './use-editor-state'
import type { EditorMediaPointerResult } from './use-editor-media-pointer'
import type { EditorDirtyStateResult } from './use-editor-dirty-state'

export function useVariantCodeMenus(c: EditorProps & EditorStateResult & EditorMediaPointerResult & EditorDirtyStateResult) {
  const { barcodeSkuMenuOpen, setBarcodeSkuMenuOpen, barcodeSkuActionRef, barcodePasteMenuOpen, setBarcodePasteMenuOpen, barcodePasteActionRef, pendingVariantCodeGeneration, setPendingVariantCodeGeneration } = c
  // The two column menus draw their list in a portal marked data-variant-header-popover.
  useDismissable(barcodeSkuMenuOpen, [barcodeSkuActionRef], () => setBarcodeSkuMenuOpen(false), { isInside: insideSelector('[data-variant-header-popover="true"]') })
  useDismissable(barcodePasteMenuOpen, [barcodePasteActionRef], () => setBarcodePasteMenuOpen(false), { isInside: insideSelector('[data-variant-header-popover="true"]') })
  useEffect(() => {
    if (!pendingVariantCodeGeneration) return
    function closeCodeGenerationOnEscape(event: KeyboardEvent) {
      if (event.key === 'Escape') setPendingVariantCodeGeneration(null)
    }
    document.addEventListener('keydown', closeCodeGenerationOnEscape)
    return () => document.removeEventListener('keydown', closeCodeGenerationOnEscape)
  // The page's own dependency list, unchanged by the split: the names it leaves out are useEditorState's setters and refs.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pendingVariantCodeGeneration])
  return {  }
}
export type VariantCodeMenusResult = ReturnType<typeof useVariantCodeMenus>
