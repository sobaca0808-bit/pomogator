import { blocksOf, kinds, kindOf, priorities, repeatValues, states, widgets, withLegacyFields,
  type Block, type CardFields, type Widget } from './cards'
import { icons, importData, safeLink, validAmount, validDate, type Data } from './model'
import { readShortcuts } from './shortcuts'

const object = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value)
const string = (value: unknown): value is string => typeof value === 'string'
const nonempty = (value: unknown): value is string => string(value) && value.trim().length > 0
const validTime = (value: string) => value === '' || /^(?:[01]\d|2[0-3]):[0-5]\d$/.test(value)
const validReminder = (value: string) => value === '' ||
  (/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/.test(value) && validDate(value.slice(0, 10)) && validTime(value.slice(11)))
const timestamp = (value: unknown): value is string => string(value) && Number.isFinite(Date.parse(value)) &&
  new Date(value).toISOString() === value
const invalid = () => new Error('В резервной копии обнаружены недопустимые карточки, блоки или вложения.')
const member = <T extends string>(choices: readonly T[], value: unknown): value is T => choices.includes(value as T)

function readBlock(raw: unknown, seen: Set<string>): Block {
  if (!object(raw) || !nonempty(raw.id) || seen.has(raw.id)) throw invalid()
  const id = raw.id
  seen.add(id)
  switch (raw.type) {
    case 'text':
      if (string(raw.value) && member(['paragraph', 'heading', 'list'] as const, raw.format))
        return { id, type: raw.type, value: raw.value, format: raw.format }
      break
    case 'link':
      if (string(raw.value) && (!raw.value || safeLink(raw.value)) && string(raw.label))
        return { id, type: raw.type, value: raw.value, label: raw.label }
      break
    case 'date': case 'time': case 'reminder': case 'amount': case 'tag': case 'relation': {
      const validate = { date: validDate, time: validTime, reminder: validReminder, amount: validAmount,
        tag: () => true, relation: () => true }[raw.type]
      if (string(raw.value) && validate(raw.value)) return { id, type: raw.type, value: raw.value }
      break
    }
    case 'status':
      if (member(states, raw.value)) return { id, type: raw.type, value: raw.value }
      break
    case 'checklist':
      if (Array.isArray(raw.items)) return { id, type: raw.type, items: raw.items.map(item => {
        if (!object(item) || !string(item.text) || typeof item.done !== 'boolean') throw invalid()
        return { text: item.text, done: item.done }
      }) }
      break
    case 'table': {
      const rows = raw.rows
      if (Array.isArray(rows) && rows.length && rows.every(row => Array.isArray(row) &&
          row.length > 0 && row.length === rows[0].length && row.every(string))) return { id, type: raw.type, rows }
      break
    }
    case 'contact':
      if (string(raw.name) && string(raw.email) && string(raw.phone))
        return { id, type: raw.type, name: raw.name, email: raw.email, phone: raw.phone }
      break
    case 'file': case 'image':
      if (string(raw.name) && string(raw.description)) {
        if (raw.file === null) return { id, type: raw.type, name: raw.name, description: raw.description, file: null }
        const file = raw.file
        if (!nonempty(raw.name) || !object(file) || !string(file.base64) || !string(file.mime) ||
            !/^[\x20-\x7e]*$/.test(file.mime) || !Number.isSafeInteger(file.size)) break
        try {
          const decoded = atob(file.base64)
          if (decoded.length !== file.size || btoa(decoded) !== file.base64) throw invalid()
          const bytes = Uint8Array.from(decoded, char => char.charCodeAt(0))
          return { id, type: raw.type, name: raw.name, description: raw.description, file: new Blob([bytes], { type: file.mime }) }
        } catch { throw invalid() }
      }
  }
  throw invalid()
}

function extraFields(raw: unknown): CardFields {
  if (!object(raw) || !Array.isArray(raw.blocks)) throw invalid()
  const seen = new Set<string>()
  const result: CardFields = { blocks: raw.blocks.map(block => readBlock(block, seen)) }
  if (raw.kind !== undefined) { if (!member(kinds, raw.kind)) throw invalid(); result.kind = raw.kind }
  if (raw.priority !== undefined) { if (!member(priorities, raw.priority)) throw invalid(); result.priority = raw.priority }
  if (raw.repeat !== undefined) { if (!member(repeatValues, raw.repeat)) throw invalid(); result.repeat = raw.repeat }
  if (raw.favorite !== undefined) { if (typeof raw.favorite !== 'boolean') throw invalid(); result.favorite = raw.favorite }
  if (raw.icon !== undefined) { if (!member(icons, raw.icon)) throw invalid(); result.icon = raw.icon }
  if (raw.direction !== undefined) {
    if (!member(['income', 'expense'] as const, raw.direction)) throw invalid()
    result.direction = raw.direction
  }
  if (raw.category !== undefined) { if (!string(raw.category)) throw invalid(); result.category = raw.category }
  if (raw.parentCard !== undefined) { if (!nonempty(raw.parentCard)) throw invalid(); result.parentCard = raw.parentCard }
  if (raw.revision !== undefined) {
    if (typeof raw.revision !== 'number' || !Number.isSafeInteger(raw.revision) || raw.revision < 0) throw invalid()
    result.revision = raw.revision
  }
  if (raw.history !== undefined) {
    if (!Array.isArray(raw.history)) throw invalid()
    result.history = raw.history.map(item => {
      if (!object(item) || !timestamp(item.at) || !nonempty(item.note)) throw invalid()
      return { at: item.at, note: item.note }
    })
  }
  return result
}

export function importBackup(text: string): Data {
  let raw: unknown
  try { raw = JSON.parse(text) } catch { throw invalid() }
  if (!object(raw)) throw invalid()
  if (raw.version === 1) {
    const data = importData(text)
    return { ...data, entries: data.entries.map(entry => ({ ...entry, kind: kindOf(data, entry), blocks: blocksOf(entry) })) }
  }
  if (raw.version !== 2 || !Array.isArray(raw.entries)) throw invalid()
  const base = importData(JSON.stringify({ version: 1, places: raw.places, entries: raw.entries }))
  const source = raw.entries
  const entries = base.entries.map((entry, index) => withLegacyFields({ ...entry, ...extraFields(source[index]) }))
  const parents = new Map(entries.map(entry => [entry.id, entry.parentCard]))
  for (const entry of entries) {
    const seen = new Set([entry.id])
    for (let id = entry.parentCard; id; id = parents.get(id)) {
      if (seen.has(id)) throw invalid()
      seen.add(id)
    }
  }
  const shortcuts = raw.shortcuts === undefined ? undefined : readShortcuts(raw.shortcuts)
  if (raw.preferences === undefined) return { ...base, entries, shortcuts }
  const prefs = raw.preferences
  const validWidgetList = (list: unknown): list is Widget[] => Array.isArray(list) &&
    list.every(item => member(widgets, item)) && new Set(list).size === list.length
  if (!object(prefs) || !validWidgetList(prefs.order) || prefs.order.length !== widgets.length ||
      !validWidgetList(prefs.hidden) || !Array.isArray(prefs.seenReminders) || !prefs.seenReminders.every(string)) throw invalid()
  return { ...base, entries, shortcuts, preferences: { order: prefs.order, hidden: prefs.hidden, seenReminders: prefs.seenReminders } }
}

async function encodeBlock(block: Block) {
  if ((block.type !== 'file' && block.type !== 'image') || block.file === null) return block
  const bytes = new Uint8Array(await block.file.arrayBuffer())
  let base64 = ''
  for (let index = 0; index < bytes.length; index += 30_000)
    base64 += btoa(String.fromCharCode(...bytes.subarray(index, index + 30_000)))
  return { ...block, file: { mime: block.file.type, size: bytes.length, base64 } }
}

export async function exportBackup(data: Data): Promise<string> {
  const entries = []
  for (const entry of data.entries) {
    const blocks = []
    for (const block of blocksOf(entry)) blocks.push(await encodeBlock(block))
    entries.push({ ...withLegacyFields(entry), blocks })
  }
  return JSON.stringify({ version: 2, places: data.places, entries, preferences: data.preferences, shortcuts: data.shortcuts }, null, 2)
}

export function validateBlocks(blocks: Block[]): string | undefined {
  for (const block of blocks) {
    if (block.type === 'link' && block.value && !safeLink(block.value)) return 'Укажите для ссылки полный адрес с http:// или https://.'
    if (block.type === 'date' && !validDate(block.value)) return 'Укажите корректную дату.'
    if (block.type === 'time' && !validTime(block.value)) return 'Укажите корректное время.'
    if (block.type === 'reminder' && !validReminder(block.value)) return 'Укажите дату и время напоминания.'
    if (block.type === 'amount' && !validAmount(block.value)) return 'Введите сумму с точностью до двух знаков после запятой.'
  }
}
