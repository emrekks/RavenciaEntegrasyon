import DOMPurify from 'dompurify'

const richTextTags = [
  'a', 'b', 'blockquote', 'br', 'code', 'div', 'em', 'h1', 'h2', 'h3', 'h4', 'h5', 'h6',
  'i', 'img', 'li', 'ol', 'p', 'pre', 's', 'strong', 'u', 'ul',
]

const richTextAttributes = ['alt', 'height', 'href', 'rel', 'src', 'target', 'title', 'width']

const richTextSanitizeConfig = {
  ALLOWED_TAGS: richTextTags,
  ALLOWED_ATTR: richTextAttributes,
  ALLOW_DATA_ATTR: false,
  ALLOW_UNKNOWN_PROTOCOLS: false,
  FORBID_ATTR: ['class', 'id', 'style'],
  FORBID_TAGS: ['embed', 'form', 'iframe', 'input', 'link', 'meta', 'object', 'script', 'style', 'svg', 'template'],
  // Product descriptions may use external HTTPS resources and safe mail/tel links.
  // Protocol-relative, data:, javascript:, and vbscript: URLs remain disallowed.
  ALLOWED_URI_REGEXP: /^(?:https?:\/\/|mailto:|tel:|\/(?!\/)|#)/i,
}

export function sanitizeRichText(value: string): string {
  return DOMPurify.sanitize(value, richTextSanitizeConfig)
}
