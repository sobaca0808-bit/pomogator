import { useEffect, useState } from 'react'
import { Download, FileText } from 'lucide-react'
import type { Attachment } from './cards'

export function downloadFile(blob: Blob, name: string) {
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = name
  link.click()
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}

export function AttachmentView({ block, preview = false }: { block: Attachment; preview?: boolean }) {
  const [image, setImage] = useState<string>()
  const [text, setText] = useState<{ file: Blob; content: string }>()
  const file = block.file
  const content = text?.file === file ? text?.content : undefined
  const imagePreview = preview && file && /^image\/(png|jpeg|webp|gif|avif)$/.test(file.type)
  const textPreview = preview && file?.type === 'text/plain'
  useEffect(() => {
    if (!file || !imagePreview) return
    const url = URL.createObjectURL(file)
    setImage(url)
    return () => URL.revokeObjectURL(url)
  }, [file, imagePreview])

  if (!file) return null
  return <div className="attachment">
    {imagePreview && image && <img src={image} alt={block.description || block.name} />}
    <div className="attachment-info"><FileText size={19} /><span><strong>{block.name}</strong>
      {block.description && <small>{block.description}</small>}</span>
      <button type="button" className="icon-button" aria-label={`Скачать ${block.name}`} onClick={() => downloadFile(file, block.name)}><Download size={17} /></button>
    </div>
    {textPreview && <button type="button" onClick={() => {
      if (content !== undefined) { setText(undefined); return }
      void file.slice(0, 65536).text().then(value => setText({ file, content: value + (file.size > 65536 ? '\n…' : '') }),
        () => setText({ file, content: 'Не удалось прочитать файл.' }))
    }}>{content === undefined ? 'Просмотреть текст' : 'Закрыть просмотр'}</button>}
    {textPreview && content !== undefined && <pre>{content}</pre>}
  </div>
}
