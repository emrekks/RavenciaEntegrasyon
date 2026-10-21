// Bulk Satışa Aç / Satışa Kapat: the selection goes out in chunks, one after the other. A failed chunk stops the run;
// the chunks before it are already saved, so the result names what went through and what did not.

export type ChunkRunResult = { doneIds: string[]; remainingIds: string[]; error: unknown }

export async function runInChunks(ids: readonly string[], chunkSize: number, send: (chunk: string[]) => Promise<unknown>, onProgress?: (done: number, total: number) => void): Promise<ChunkRunResult> {
  const doneIds: string[] = []
  for (let index = 0; index < ids.length; index += chunkSize) {
    const chunk = ids.slice(index, index + chunkSize)
    try {
      await send(chunk)
    } catch (error) {
      return { doneIds, remainingIds: ids.slice(index), error }
    }
    doneIds.push(...chunk)
    onProgress?.(doneIds.length, ids.length)
  }
  return { doneIds, remainingIds: [], error: null }
}

export function bulkStatusResultMessage(statusLabel: string, result: ChunkRunResult): { message: string; kind: 'success' | 'error' } {
  const reason = result.error instanceof Error ? result.error.message : 'Toplu durum güncelleme başarısız.'
  if (!result.remainingIds.length) return { message: `${result.doneIds.length} ürün durumu “${statusLabel}” olarak güncellendi.`, kind: 'success' }
  if (!result.doneIds.length) return { message: reason, kind: 'error' }
  return { message: `${result.doneIds.length} ürün “${statusLabel}” olarak güncellendi; kalan ${result.remainingIds.length} ürün güncellenemedi: ${reason}`, kind: 'error' }
}
