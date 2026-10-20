// Invisible control characters (C0 except tab and line breaks, DEL and the C1 block) carry no meaning in the panel but
// draw as boxes; one slipped into an imported colour name ("Sarı" + U+008D). They are dropped from every text the
// server sends.
// eslint-disable-next-line no-control-regex -- matching control characters is the point of this pattern
const invisibleControls = /[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f-\u009f]/g
const dropInvisibleControls = (_key: string, value: unknown) => typeof value === 'string' ? value.replace(invisibleControls, '') : value

export async function readSuccessJson<T>(response: Response): Promise<T> {
  const body = await response.text()
  if (!body.trim()) throw new Error('Sunucudan boş yanıt geldi. Sayfayı yenileyip tekrar deneyin.')
  try { return JSON.parse(body, dropInvisibleControls) as T }
  catch { throw new Error('Sunucudan geçerli bir yanıt alınamadı. Sayfayı yenileyip tekrar deneyin.') }
}
