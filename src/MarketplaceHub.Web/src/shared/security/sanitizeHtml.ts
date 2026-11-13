import createDOMPurify from 'dompurify'

const richTextTags = [
  'a', 'b', 'blockquote', 'br', 'code', 'div', 'em', 'h1', 'h2', 'h3', 'h4', 'h5', 'h6',
  'i', 'img', 'li', 'ol', 'p', 'pre', 's', 'span', 'strong', 'u', 'ul',
]

const richTextAttributes = ['alt', 'height', 'href', 'rel', 'src', 'style', 'target', 'title', 'width']

// The description editor's colour, size and alignment tools write inline styles. Only these properties, on text
// elements, with plain values survive; url(), expression(), escapes, comments and every other property are dropped.
const styledTags = new Set(['b', 'blockquote', 'div', 'em', 'h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'i', 'li', 'p', 's', 'span', 'strong', 'u'])
const colorValue = /^(?:#[0-9a-f]{3,8}|rgba?\(\s*\d{1,3}%?\s*,\s*\d{1,3}%?\s*,\s*\d{1,3}%?\s*(?:,\s*(?:0|1|0?\.\d+)\s*)?\)|[a-z]{3,20})$/i
const fontSizeValue = /^(?:xx-small|x-small|small|medium|large|x-large|xx-large|xxx-large|smaller|larger|\d{1,3}(?:\.\d{1,2})?(?:px|pt|em|rem|%))$/i
const textAlignValue = /^(?:left|right|center|justify|start|end)$/i
const allowedStyles: Record<string, RegExp> = { color: colorValue, 'font-size': fontSizeValue, 'text-align': textAlignValue }

/** The allowed declarations of an inline style, rewritten in a fixed form; empty when none is allowed. */
export function safeInlineStyle(style: string): string {
  if (/[\\<>]|\/\*|url\s*\(|expression|javascript:|@import/i.test(style)) return ''
  const kept = new Map<string, string>()
  for (const declaration of style.split(';')) {
    const separator = declaration.indexOf(':')
    if (separator < 0) continue
    const property = declaration.slice(0, separator).trim().toLowerCase()
    const value = declaration.slice(separator + 1).trim().replace(/\s*!important$/i, '')
    const pattern = allowedStyles[property]
    if (pattern?.test(value) && !/^(?:inherit|initial|unset|revert|currentcolor)$/i.test(value)) kept.set(property, value)
  }
  return [...kept].map(([property, value]) => `${property}: ${value}`).join('; ')
}

const richTextSanitizeConfig = {
  ALLOWED_TAGS: richTextTags,
  ALLOWED_ATTR: richTextAttributes,
  ALLOW_DATA_ATTR: false,
  ALLOW_UNKNOWN_PROTOCOLS: false,
  FORBID_ATTR: ['class', 'id'],
  FORBID_TAGS: ['embed', 'form', 'iframe', 'input', 'link', 'meta', 'object', 'script', 'style', 'svg', 'template'],
  // Product descriptions may use external HTTPS resources and safe mail/tel links.
  // Protocol-relative, data:, javascript:, and vbscript: URLs remain disallowed.
  ALLOWED_URI_REGEXP: /^(?:https?:\/\/|mailto:|tel:|\/(?!\/)|#)/i,
}

// An instance of its own, so the style hook never reaches another sanitizer.
const purifier = createDOMPurify(window)
purifier.addHook('uponSanitizeAttribute', (node, data) => {
  if (data.attrName !== 'style') return
  const safe = styledTags.has(node.nodeName.toLowerCase()) ? safeInlineStyle(data.attrValue) : ''
  if (safe) data.attrValue = safe
  else data.keepAttr = false
})

export function sanitizeRichText(value: string): string {
  return purifier.sanitize(value, richTextSanitizeConfig)
}
