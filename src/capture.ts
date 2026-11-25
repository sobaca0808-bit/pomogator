import { createCard, type Block } from './cards'
import { saveEntry, type Data, type Place } from './model'

const inboxId = 'pomogator:inbox'
export const inboxTitle = 'Входящие'

export function inboxFor(data: Data): Place {
  const places = new Map(data.places.map(place => [place.id, place]))
  for (let index = 0; ; index++) {
    const id = index === 0 ? inboxId : `${inboxId}:${index}`
    const existing = places.get(id)
    if (!existing) return { id, parent: null, title: inboxTitle, icon: 'folder' }
    if (existing.parent === null && existing.title === inboxTitle && existing.icon === 'folder') return existing
  }
}

export function withInbox(data: Data): Data {
  const inbox = inboxFor(data)
  return data.places.includes(inbox) ? data : { ...data, places: [...data.places, inbox] }
}

export type CaptureDraft = { kind: 'note' | 'task'; text: string; parent: string; file?: File }
export const emptyCapture: CaptureDraft = { kind: 'note', text: '', parent: '' }

export function saveCapture(data: Data, draft: CaptureDraft): Data {
  const value = draft.text.trim()
  if (!value && !draft.file) throw new Error('Введите текст или приложите файл.')
  const parent = draft.parent || inboxFor(data).id
  const card = createCard(draft.kind, parent)
  const blocks: Block[] = card.blocks!.filter(block => block.type !== 'text')
  if (value) blocks.push({ id: crypto.randomUUID(), type: 'text', value, format: 'paragraph' })
  if (draft.file) {
    const file = draft.file
    blocks.push({ id: crypto.randomUUID(), type: file.type.startsWith('image/') ? 'image' : 'file',
      name: file.name, description: '', file: file.slice(0, file.size, file.type) })
  }
  const title = Array.from(value.split(/\r?\n/, 1)[0] || draft.file!.name).slice(0, 100).join('')
  return saveEntry(withInbox(data), { ...card, title, blocks })
}
