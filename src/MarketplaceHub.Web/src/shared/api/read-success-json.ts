export async function readSuccessJson<T>(response: Response): Promise<T> {
  const body = await response.text()
  if (!body.trim()) throw new Error('Sunucudan boş yanıt geldi. Sayfayı yenileyip tekrar deneyin.')
  try { return JSON.parse(body) as T }
  catch { throw new Error('Sunucudan geçerli bir yanıt alınamadı. Sayfayı yenileyip tekrar deneyin.') }
}
