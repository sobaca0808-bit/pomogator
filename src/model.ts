import type { Shortcut } from './shortcuts'
import { blocksOf, blockStrings, kindName, kindOf, withLegacyFields, type CardFields, type Preferences } from './cards'

export const icons = ['projects', 'finance', 'tasks', 'notes', 'docs', 'services', 'folder', 'star'] as const
export type Icon = typeof icons[number]
export type Place = { id: string; parent: string | null; title: string; icon: Icon }
export type Entry = CardFields & {
  id: string; parent: string; title: string; text: string; links: string[]
  checklist: { text: string; done: boolean }[]
  date: string; amount: string; created: string; updated: string
}
export type Data = { version: 1; places: Place[]; entries: Entry[]; preferences?: Preferences; shortcuts?: Shortcut[]; generation?: string }
export type Change = (data: Data) => Data

const defaults = [
  ['Мои проекты', 'projects'], ['Финансы', 'finance'], ['Мои дела', 'tasks'],
  ['Заметки', 'notes'], ['Документы', 'docs'], ['Мои сервисы', 'services'],
] as const

export function seed(): Data {
  return { version: 1, places: defaults.map(([title, icon]) => ({
    id: icon, parent: null, title, icon,
  })), entries: [] }
}

function requirePlace(data: Data, id: string) {
  if (!data.places.some(place => place.id === id)) throw new Error('Папка или раздел уже удалены.')
}

export function savePlace(data: Data, place: Place, editing = false): Data {
  if (place.parent !== null) requirePlace(data, place.parent)
  if (editing) requirePlace(data, place.id)
  return { ...data, places: editing
    ? data.places.map(item => item.id === place.id ? { ...item, title: place.title, icon: place.icon } : item)
    : [...data.places, place] }
}

export function orderPlace(data: Data, id: string, offset: -1 | 1, skip?: string): Data {
  const places = [...data.places]
  const index = places.findIndex(place => place.id === id)
  if (index === -1) return data
  for (let next = index + offset; next >= 0 && next < places.length; next += offset) {
    if (places[next].id !== skip && places[next].parent === places[index].parent) {
      [places[index], places[next]] = [places[next], places[index]]
      break
    }
  }
  return { ...data, places }
}

export function contents(data: Data, id: string) {
  const children = new Map<string, string[]>()
  for (const place of data.places) {
    if (place.parent === null) continue
    const siblings = children.get(place.parent) ?? []
    siblings.push(place.id)
    children.set(place.parent, siblings)
  }
  const ids = new Set([id])
  for (const parent of ids) {
    for (const child of children.get(parent) ?? []) ids.add(child)
  }
  return { ids, entries: data.entries.filter(entry => ids.has(entry.parent)) }
}

export function deletePlace(data: Data, id: string, confirmed = false): Data {
  const { ids, entries } = contents(data, id)
  if (!confirmed && (ids.size > 1 || entries.length)) {
    throw new Error('В разделе или папке появилось содержимое. Подтвердите удаление повторно.')
  }
  return { ...data, places: data.places.filter(place => !ids.has(place.id)),
    entries: data.entries.filter(entry => !ids.has(entry.parent)) }
}

export function saveEntry(data: Data, entry: Entry, editing = false, expectedRevision?: number): Data {
  requirePlace(data, entry.parent)
  const existing = data.entries.find(item => item.id === entry.id)
  if (editing && !existing) throw new Error('Запись уже удалена.')
  if (!editing && existing) throw new Error('Карточка уже существует.')
  if (expectedRevision !== undefined && (existing?.revision ?? 0) !== expectedRevision)
    throw new Error('Карточка изменилась в другой вкладке. Откройте её заново.')
  if (entry.parentCard && entry.parentCard !== existing?.parentCard) {
    const byId = new Map(data.entries.map(card => [card.id, card]))
    if (!byId.has(entry.parentCard)) throw new Error('Родительская карточка уже удалена.')
    for (let id: string | undefined = entry.parentCard; id; id = byId.get(id)?.parentCard) {
      if (id === entry.id) throw new Error('Вложенность карточек не может образовывать цикл.')
    }
  }
  const updated = new Date().toISOString()
  const saved = { ...withLegacyFields(entry), created: existing?.created ?? updated, updated }
  if (entry.blocks) {
    saved.revision = (existing?.revision ?? 0) + 1
    saved.history = [{ at: updated, note: !existing ? 'Карточка создана' :
      existing.parent !== entry.parent ? 'Карточка перемещена' : 'Карточка обновлена' }, ...(existing?.history ?? [])].slice(0, 30)
  }
  return { ...data, entries: editing
    ? data.entries.map(item => item.id === entry.id ? saved : item)
    : [...data.entries, saved] }
}

export function deleteEntry(data: Data, id: string): Data {
  return { ...data, entries: data.entries.filter(entry => entry.id !== id) }
}

export function restoreDeleted(data: Data, places: Place[], entries: Entry[]): Data {
  const existingPlaces = new Set(data.places.map(place => place.id))
  const available = new Set([...existingPlaces, ...places.map(place => place.id)])
  const existingEntries = new Set(data.entries.map(entry => entry.id))
  if (places.some(place => existingPlaces.has(place.id) || (place.parent !== null && !available.has(place.parent))) ||
    entries.some(entry => existingEntries.has(entry.id) || !available.has(entry.parent)))
    throw new Error('Не удалось отменить удаление: связанные папки или карточки изменились.')
  return { ...data, places: [...data.places, ...places], entries: [...data.entries, ...entries] }
}

export function pathTo(data: Data, id: string): Place[] {
  const byId = new Map(data.places.map(place => [place.id, place]))
  const path: Place[] = []
  for (let place = byId.get(id); place; place = byId.get(place.parent ?? '')) path.push(place)
  return path.reverse()
}

export function search(data: Data, query: string) {
  const term = query.trim().toLocaleLowerCase('ru')
  const matches = (parts: string[]) => parts.some(part => part.toLocaleLowerCase('ru').includes(term))
  return {
    places: term ? data.places.filter(place => matches([place.title])) : [],
    entries: term ? data.entries.filter(entry => matches([
      entry.title, entry.text, entry.date, entry.amount, ...entry.links,
      ...entry.checklist.map(item => item.text), ...blocksOf(entry).flatMap(blockStrings), entry.category ?? '',
      kindName[kindOf(data, entry)],
    ])) : [],
  }
}

export function safeLink(value: string): boolean {
  try { return ['https:', 'http:'].includes(new URL(value).protocol) } catch { return false }
}

export const validAmount = (value: string) => value === '' || /^-?\d+(\.\d{1,2})?$/.test(value)
export function validDate(value: string) {
  return value === '' || (/^\d{4}-\d{2}-\d{2}$/.test(value) &&
    !Number.isNaN(Date.parse(value)) && new Date(value).toISOString().slice(0, 10) === value)
}

const object = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value)
const name = (value: unknown): value is string => typeof value === 'string' && value.trim() !== ''
const timestamp = (value: unknown): value is string => typeof value === 'string' &&
  Number.isFinite(Date.parse(value)) && new Date(value).toISOString() === value

export function importData(text: string): Data {
  const invalid = () => new Error('Файл не соответствует формату резервной копии ПОМОГАТОР v0.1.')
  let raw: unknown
  try { raw = JSON.parse(text) } catch { throw invalid() }
  if (!object(raw) || raw.version !== 1 || !Array.isArray(raw.places) || !Array.isArray(raw.entries)) throw invalid()
  const seen = new Map<string, string | null>()
  const places = raw.places.map((item: unknown): Place => {
    if (!object(item) || !name(item.id) || !name(item.title) ||
        !(item.parent === null || name(item.parent)) || !icons.includes(item.icon as Icon) ||
        seen.has(item.id)) throw invalid()
    seen.set(item.id, item.parent)
    return { id: item.id, parent: item.parent, title: item.title, icon: item.icon as Icon }
  })
  const checked = new Set<string>()
  for (const place of places) {
    const chain = new Set<string>()
    let id: string | null = place.id
    while (id !== null && !checked.has(id)) {
      const parent: string | null | undefined = seen.get(id)
      if (chain.has(id) || parent === undefined) throw invalid()
      chain.add(id)
      id = parent
    }
    for (const item of chain) checked.add(item)
  }
  const entryIds = new Set<string>()
  const entries = raw.entries.map((item: unknown): Entry => {
    if (!object(item) || !name(item.id) || entryIds.has(item.id) || !name(item.parent) ||
        !seen.has(item.parent) || !name(item.title) || typeof item.text !== 'string' ||
        typeof item.date !== 'string' || !validDate(item.date) ||
        typeof item.amount !== 'string' || !validAmount(item.amount) ||
        !timestamp(item.created) || !timestamp(item.updated) ||
        !Array.isArray(item.links) || !Array.isArray(item.checklist)) throw invalid()
    const links = item.links.map((link: unknown) => {
      if (typeof link !== 'string' || !safeLink(link)) throw invalid()
      return link
    })
    const checklist = item.checklist.map((row: unknown) => {
      if (!object(row) || !name(row.text) || typeof row.done !== 'boolean') throw invalid()
      return { text: row.text, done: row.done }
    })
    entryIds.add(item.id)
    return { id: item.id, parent: item.parent, title: item.title, text: item.text, links,
      checklist, date: item.date, amount: item.amount, created: item.created, updated: item.updated }
  })
  return { version: 1, places, entries }
}

export const exportData = (data: Data) => JSON.stringify(data, null, 2)
