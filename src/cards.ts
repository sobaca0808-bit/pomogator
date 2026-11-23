import type { Data, Entry, Icon } from './model'

export const kinds = ['project', 'task', 'purchase', 'payment', 'note', 'document', 'link'] as const
export type Kind = typeof kinds[number]
export const kindName: Record<Kind, string> = { project: 'Проект', task: 'Задача', purchase: 'Покупка',
  payment: 'Платёж', note: 'Заметка', document: 'Документ', link: 'Ссылка' }
export const kindIcon: Record<Kind, Icon> = {
  project: 'projects', task: 'tasks', purchase: 'finance', payment: 'finance', note: 'notes', document: 'docs', link: 'services',
}

export const states = ['planned', 'active', 'done'] as const
export type Status = typeof states[number]
export const stateName: Record<Status, string> = { planned: 'Запланировано', active: 'В работе', done: 'Выполнено' }
export const priorities = ['normal', 'low', 'high'] as const
export const repeatValues = ['none', 'daily', 'weekly', 'monthly'] as const
export type Repeat = typeof repeatValues[number]
export const repeatName: Record<Repeat, string> = { none: 'Без повтора', daily: 'Ежедневно', weekly: 'Еженедельно', monthly: 'Ежемесячно' }
export const priorityName = { normal: 'Обычный', low: 'Низкий', high: 'Высокий' }
export const widgetNames = { today: 'Сегодня', tasks: 'Ближайшие дела', payments: 'Предстоящие платежи',
  projects: 'Активные проекты', recent: 'Недавние карточки', favorites: 'Избранные ссылки' }
export type Widget = keyof typeof widgetNames
export const widgets = Object.keys(widgetNames) as Widget[]
export type Preferences = { order: Widget[]; hidden: Widget[]; seenReminders: string[] }
export const preferences = (data: Data): Preferences => data.preferences ?? { order: widgets, hidden: [], seenReminders: [] }

export const blockNames = { text: 'Текст', link: 'Ссылка', date: 'Дата', time: 'Время', reminder: 'Напоминание',
  checklist: 'Чек-лист', table: 'Таблица', amount: 'Сумма', image: 'Изображение', file: 'Файл',
  contact: 'Контакт', tag: 'Тег', status: 'Статус', relation: 'Связь с карточкой' }
export type BlockType = keyof typeof blockNames
export type Item = { text: string; done: boolean }
export type Attachment = { type: 'file' | 'image'; name: string; description: string; file: Blob | null }
export type Block = { id: string } & (
  { type: 'text'; value: string; format: 'paragraph' | 'heading' | 'list' } |
  { type: 'link'; value: string; label: string } |
  { type: 'date' | 'time' | 'reminder' | 'amount' | 'tag' | 'relation'; value: string } |
  { type: 'checklist'; items: Item[] } | { type: 'table'; rows: string[][] } |
  { type: 'contact'; name: string; email: string; phone: string } | { type: 'status'; value: Status } | Attachment)
export type CardFields = {
  kind?: Kind; blocks?: Block[]; favorite?: boolean; icon?: Icon; parentCard?: string;
  priority?: typeof priorities[number]; repeat?: Repeat; direction?: 'income' | 'expense'; category?: string;
  revision?: number; history?: { at: string; note: string }[];
}

export function makeBlock(type: BlockType, id = crypto.randomUUID() as string): Block {
  if (type === 'text') return { id, type, value: '', format: 'paragraph' }
  if (type === 'link') return { id, type, value: '', label: '' }
  if (type === 'checklist') return { id, type, items: [] }
  if (type === 'table') return { id, type, rows: [['', ''], ['', '']] }
  if (type === 'file' || type === 'image') return { id, type, name: '', description: '', file: null }
  if (type === 'contact') return { id, type, name: '', email: '', phone: '' }
  if (type === 'status') return { id, type, value: 'planned' }
  return { id, type, value: '' }
}

export function blocksOf(entry: Entry): Block[] {
  if (entry.blocks) return entry.blocks
  const result: Block[] = []
  const legacy = (suffix: string) => 'legacy-' + suffix + '-' + entry.id
  if (entry.text) result.push({ id: legacy('text'), type: 'text', value: entry.text, format: 'paragraph' })
  entry.links.forEach((value, index) => result.push({ id: legacy('link-' + index), type: 'link', value, label: '' }))
  if (entry.checklist.length) result.push({ id: legacy('checks'), type: 'checklist', items: entry.checklist })
  if (entry.date) result.push({ id: legacy('date'), type: 'date', value: entry.date })
  if (entry.amount) result.push({ id: legacy('amount'), type: 'amount', value: entry.amount })
  return result
}

export function kindOf(data: Data, entry: Pick<Entry, 'kind' | 'parent'>): Kind {
  if (entry.kind) return entry.kind
  const places = new Map(data.places.map(place => [place.id, place]))
  let place = places.get(entry.parent)
  while (place?.parent) place = places.get(place.parent)
  switch (place?.id) {
    case 'projects': return 'project'
    case 'tasks': return 'task'
    case 'finance': return 'payment'
    case 'docs': return 'document'
    case 'services': return 'link'
    default: return 'note'
  }
}

const templateBlocks: Record<Kind, BlockType[]> = {
  project: ['status', 'date', 'checklist', 'amount', 'text'], task: ['status', 'date', 'time', 'checklist'],
  purchase: ['amount', 'date', 'status'], payment: ['amount', 'date', 'status'],
  note: ['text'], document: ['file', 'date', 'text'], link: ['link', 'text'],
}
export const rootFor: Record<Kind, string> = {
  project: 'projects', task: 'tasks', purchase: 'finance', payment: 'finance', note: 'notes', document: 'docs', link: 'services',
}
export function createCard(kind: Kind, parent: string, date = '', parentCard?: string): Entry {
  const template = templateBlocks[kind]
  const types = date && !template.includes('date') ? [...template, 'date' as const] : template
  const blocks = types.map(type => type === 'date' ? { ...makeBlock(type), type, value: date } : makeBlock(type))
  return { id: crypto.randomUUID(), parent, title: '', text: '', links: [], checklist: [], date: '', amount: '',
    created: '', updated: '', kind, parentCard, direction: 'expense', blocks }
}

export const firstValue = (entry: Entry, type: BlockType) =>
  blocksOf(entry).flatMap(block => block.type === type && 'value' in block && block.value ? [block.value] : [])[0] ?? ''
export const cardStatus = (entry: Entry): Status => (firstValue(entry, 'status') || 'planned') as Status
export const tagsOf = (entry: Entry) => blocksOf(entry).flatMap(block => block.type === 'tag' && block.value ? [block.value] : [])
export function progress(entry: Entry): number {
  const items = blocksOf(entry).flatMap(block => block.type === 'checklist' ? block.items : []).filter(item => item.text.trim())
  return cardStatus(entry) === 'done' ? 100 : items.length ? Math.round(100 * items.filter(item => item.done).length / items.length) : 0
}

export function blockStrings(block: Block): string[] {
  if (block.type === 'checklist') return block.items.map(item => item.text)
  if (block.type === 'table') return block.rows.flat()
  if (block.type === 'file' || block.type === 'image') return [block.name, block.description]
  if (block.type === 'contact') return [block.name, block.email, block.phone]
  if (block.type === 'link') return [block.value, block.label]
  if (block.type === 'status') return [stateName[block.value]]
  return 'value' in block ? [block.value] : []
}

export function withLegacyFields(entry: Entry): Entry {
  if (!entry.blocks) return entry
  return { ...entry, text: entry.blocks.flatMap(block => block.type === 'text' ? [block.value] : []).join('\n\n'),
    links: entry.blocks.flatMap(block => block.type === 'link' && block.value ? [block.value] : []),
    checklist: entry.blocks.flatMap(block => block.type === 'checklist' ? block.items : []).filter(item => item.text.trim()),
    date: firstValue(entry, 'date'), amount: firstValue(entry, 'amount') }
}

export function setStatus(entry: Entry, value: Status): Entry {
  const blocks = blocksOf(entry)
  const first = blocks.find(block => block.type === 'status')
  return { ...entry, blocks: first ? blocks.map(block => block === first ? { ...first, value } : block) :
    [{ id: crypto.randomUUID(), type: 'status', value }, ...blocks] }
}

export function reorder<T>(items: T[], index: number, offset: number): T[] {
  const next = index + offset
  if (index < 0 || index >= items.length || next < 0 || next >= items.length) return items
  const copy = [...items]
  copy[index] = items[next]
  copy[next] = items[index]
  return copy
}
