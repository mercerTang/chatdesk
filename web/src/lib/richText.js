import { marked } from 'marked'
import DOMPurify from 'dompurify'

// 消息以 Markdown 源文存储；渲染前先转义原始 HTML 再交给 marked，
// 最后用 DOMPurify 白名单净化。图片只放行本站文件端点，避免外链追踪与注入。

marked.setOptions({ gfm: true, breaks: true })

const ALLOWED_TAGS = [
  'p', 'br', 'strong', 'em', 'del', 'code', 'pre', 'blockquote', 'ul', 'ol', 'li', 'a', 'img', 'hr'
]

const ALLOWED_ATTR = ['href', 'src', 'alt']

const LOCAL_FILE_URL = /^\/api\/files\/\d+$/

const escapeHtml = (text) =>
  text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')

/** Markdown 源文 → 可上屏的安全 HTML。 */
export function renderRich(content) {
  if (!content) {
    return ''
  }
  const html = marked.parse(escapeHtml(content))
  const clean = DOMPurify.sanitize(html, { ALLOWED_TAGS, ALLOWED_ATTR })

  // 只保留本系统上传的图片地址；外链图片一律去掉
  const template = document.createElement('template')
  template.innerHTML = clean
  template.content.querySelectorAll('img').forEach((img) => {
    const src = img.getAttribute('src') || ''
    if (!LOCAL_FILE_URL.test(src)) {
      img.remove()
    }
  })
  return template.innerHTML
}

/** Markdown 源文 → 纯文本（列表摘要、通知正文用）。 */
export function toPlainText(content) {
  if (!content) {
    return ''
  }
  return content
    .replace(/!\[([^\]]*)\]\(([^)]+)\)/g, '$1')
    .replace(/```[\s\S]*?(```|$)/g, '[代码]')
    .replace(/`([^`]*)`/g, '$1')
    .replace(/\*\*([^*]+)\*\*/g, '$1')
    .replace(/__([^_]+)__/g, '$1')
    .replace(/\*([^*]+)\*/g, '$1')
    .replace(/~~([^~]+)~~/g, '$1')
    .replace(/^>\s?/gm, '')
    .replace(/^[-*+]\s+/gm, '')
    .replace(/\[([^\]]+)\]\(([^)]+)\)/g, '$1')
    .replace(/\s+/g, ' ')
    .trim()
}
