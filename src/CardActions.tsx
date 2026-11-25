import { useState } from 'react'
import { blocksOf, stateName } from './cards'
import { AttachmentView } from './attachments'
import { Dialog } from './editors'
import { pathTo, type Data, type Entry } from './model'

export function MoveCard({ entry, data, busy, error, close, move }: {
  entry: Entry; data: Data; busy: boolean; error: string; close: () => void; move: (parent: string) => void
}) {
  const [parent, setParent] = useState(entry.parent)
  return <Dialog title="Переместить карточку" close={close}>
    <form onSubmit={event => { event.preventDefault(); move(parent) }}>
      <label>Куда переместить<select value={parent} onChange={event => setParent(event.target.value)}>
        {data.places.map(place => <option key={place.id} value={place.id}>{pathTo(data, place.id).map(p => p.title).join(' / ')}</option>)}
      </select></label>
      {error && <p className="error" role="alert">{error}</p>}
      <button className="primary" disabled={busy || parent === entry.parent}>Переместить</button>
    </form>
  </Dialog>
}

export function shareContent(entry: Entry) {
  const lines = [entry.title]
  const files: File[] = []
  for (const block of blocksOf(entry)) {
    switch (block.type) {
      case 'image': case 'file':
        if (block.file) files.push(new File([block.file], block.name, { type: block.file.type }))
        if (block.description) lines.push(block.description)
        break
      case 'checklist': lines.push(...block.items.map(item => (item.done ? '☑ ' : '☐ ') + item.text)); break
      case 'table': lines.push(...block.rows.map(row => row.join(' · '))); break
      case 'contact': lines.push([block.name, block.email, block.phone].filter(Boolean).join(' ')); break
      case 'status': lines.push(stateName[block.value]); break
      case 'relation': break
      default: if (block.value && block.value !== entry.title) lines.push(block.value)
    }
  }
  return { title: entry.title, text: lines.join('\n'), ...(files.length ? { files } : {}) }
}

export async function shareCard(entry: Entry, fallback: () => void) {
  try {
    const payload = shareContent(entry)
    if (!navigator.share || (navigator.canShare ? !navigator.canShare(payload) : payload.files)) return fallback()
    await navigator.share(payload)
  } catch (error) {
    if (!(error instanceof DOMException && error.name === 'AbortError')) fallback()
  }
}

export function ShareFallback({ entry, close }: { entry: Entry; close: () => void }) {
  return <Dialog title="Поделиться карточкой" close={close}>
    <p>Системная отправка недоступна. Скопируйте текст и скачайте нужные вложения.</p>
    <textarea aria-label="Текст для отправки" readOnly rows={5} value={shareContent(entry).text} onFocus={event => event.target.select()} />
    {blocksOf(entry).map(block => (block.type === 'image' || block.type === 'file') && <AttachmentView key={block.id} block={block} />)}
  </Dialog>
}
