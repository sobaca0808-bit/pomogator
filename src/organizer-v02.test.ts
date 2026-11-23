import 'fake-indexeddb/auto'
import { describe, expect, it } from 'vitest'
import { exportBackup, importBackup } from './backup'
import { blocksOf, createCard, firstValue, makeBlock, preferences, progress, reorder, setStatus, type Block } from './cards'
import { financeCsv, financeTotals, operations } from './finance'
import { deletePlace, exportData, restoreDeleted, saveEntry, savePlace, search, seed, type Data } from './model'
import { dayEntries, dueReminders, occursOn, upcoming } from './schedule'
import { openStorage, transact } from './storage'

const text = (value: string): Block => ({ id: 'text', type: 'text', format: 'heading', value })
const task = (title: string) => ({ ...createCard('task', 'tasks'), title })
const date = (value: string): Block => ({ id: 'date', type: 'date', value })

describe('Карточки v0.2', () => {
  it('читает сохранённую базу v0.1 в том же хранилище и сохраняет новую редакцию', async () => {
    const legacy = { ...task('Запись из первой версии'), blocks: undefined, kind: undefined,
      text: 'Описание', links: ['https://example.org'], amount: '7.25', date: '2026-01-31' }
    const db = await openStorage('organizer-legacy')
    const first = await transact(db, data => saveEntry(data, legacy))
    db.close()
    const reopened = await openStorage('organizer-legacy')
    const existing = (await transact(reopened)).entries[0]
    expect(blocksOf(existing).map(block => block.type)).toEqual(['text', 'link', 'date', 'amount'])
    const migrated = importBackup(exportData(first))
    expect(migrated.entries[0]).toMatchObject({ kind: 'task', title: existing.title, created: existing.created })
    const saved = await transact(reopened, data => saveEntry(data, { ...existing, blocks: [...blocksOf(existing), text('Правка')] }, true, 0))
    expect(saved.entries[0]).toMatchObject({ text: 'Описание\n\nПравка', created: existing.created, revision: 1 })
    await expect(transact(reopened, data => saveEntry(data, { ...existing, title: 'Устарело' }, true, 0))).rejects.toThrow(/другой вкладке/)
    reopened.close()
  })

  it('поддерживает 14 типов блоков, перестановку, поиск и расчёт выполнения', () => {
    const project = { ...createCard('project', 'projects'), title: 'Ремонт',
      blocks: [text('Смета'), { id: 'check', type: 'checklist', items: [{ text: 'Акты', done: true }, { text: 'Доставка', done: false }] },
        { id: 'table', type: 'table', rows: [['Ламинат']] }, { id: 'tag', type: 'tag', value: 'Квартира' },
        { id: 'link', type: 'link', value: 'https://example.org', label: 'Договор' }] } satisfies ReturnType<typeof createCard>
    const data = saveEntry(seed(), project)
    expect(progress(project)).toBe(50)
    expect(progress(setStatus(project, 'done'))).toBe(100)
    expect(reorder(project.blocks, 0, 1)[1].id).toBe('text')
    for (const term of ['АКТЫ', 'ДОГОВОР', 'КВАРТИРА', 'ЛАМИНАТ']) expect(search(data, term).entries[0].title).toBe('Ремонт')
    expect(firstValue(createCard('note', 'notes', '2026-11-24'), 'date')).toBe('2026-11-24')
  })

  it('не допускает циклы подзадач и восстанавливает удалённую папку без потери соседних изменений', () => {
    const place = { id: 'nested', title: 'Работа', parent: 'tasks', icon: 'folder' } as const
    const main = { ...task('Подготовить'), parent: place.id }
    const child = { ...task('Забрать'), parent: place.id, parentCard: main.id }
    const data = saveEntry(saveEntry(savePlace(seed(), place), main), child)
    expect(() => saveEntry(data, { ...data.entries[0], parentCard: child.id }, true)).toThrow(/цикл/)
    const extra = task('Создано после удаления')
    const after = saveEntry(deletePlace(data, place.id, true), extra)
    const restored = restoreDeleted(after, [place], data.entries)
    expect(restored.entries.map(entry => entry.id)).toEqual([extra.id, main.id, child.id])
    expect(() => restoreDeleted(restored, [place], data.entries)).toThrow()
  })
})

describe('Резервные копии', () => {
  it('возвращает двоичные вложения, настройки виджетов и все типы блоков', async () => {
    const types = ['text', 'link', 'date', 'time', 'reminder', 'checklist', 'table', 'amount', 'image',
      'file', 'contact', 'tag', 'status', 'relation'] as const
    const bytes = Uint8Array.from({ length: 60031 }, (_, index) => index % 251)
    const file = new Blob([bytes], { type: 'application/octet-stream' })
    const blocks = types.map(type => type === 'file' ? { id: 'binary', type, file, description: 'Вложение', name: 'document.bin' } : makeBlock(type))
    let data = saveEntry(seed(), { ...createCard('document', 'docs'), title: 'Архив', blocks })
    data = { ...data, preferences: { ...preferences(data), hidden: ['recent'], seenReminders: ['test'] } }
    const imported = importBackup(await exportBackup(data))
    const db = await openStorage('organizer-attachments')
    await transact(db, () => imported)
    db.close()
    const reopened = await openStorage('organizer-attachments')
    const current = await transact(reopened)
    const restored = blocksOf(current.entries[0]).find(block => block.type === 'file')
    expect(blocksOf(current.entries[0]).map(block => block.type)).toEqual(types)
    if (restored?.type !== 'file' || !restored.file) throw new Error('Вложение отсутствует')
    expect(restored.file.type).toBe('application/octet-stream')
    expect(new Uint8Array(await restored.file.arrayBuffer())).toEqual(bytes)
    expect(current.preferences).toEqual(data.preferences)
    reopened.close()
  })

  it('отклоняет повреждённые вложения, дубликаты и опасные ссылки до записи', async () => {
    const data = saveEntry(seed(), task('Сохранённый оригинал'))
    const serialized = JSON.parse(await exportBackup(data))
    const db = await openStorage('organizer-bad-backup')
    await transact(db, () => data)
    for (const block of [
      { id: 'x', type: 'file', name: 'a', description: '', file: { mime: '', size: 3, base64: 'YQ==' } },
      { id: serialized.entries[0].blocks[0].id, type: 'tag', value: 'дубль' },
      { id: 'x', type: 'link', value: 'javascript:alert(1)', label: '' },
      { id: 'x', type: 'table', rows: [['1'], ['1', '2']] },
    ]) {
      const candidate = { ...serialized, entries: [{ ...serialized.entries[0], blocks: [...serialized.entries[0].blocks, block] }] }
      expect(() => importBackup(JSON.stringify(candidate))).toThrow()
    }
    expect(await transact(db)).toEqual(data)
    db.close()
  })
})

describe('Сроки и финансы', () => {
  it('учитывает короткий месяц, недельные повторы и завершённые дела', () => {
    const entry = { ...task('Отчёт'), blocks: [date('2026-01-31')], repeat: 'monthly' as const }
    expect(occursOn('2026-01-31', '2026-02-28', 'monthly')).toBe(true)
    expect(occursOn('2026-01-31', '2026-03-28', 'monthly')).toBe(false)
    expect(occursOn('2026-03-28', '2026-04-04', 'weekly')).toBe(true)
    const data = saveEntry(seed(), entry)
    expect(dayEntries(data, '2026-02-28').map(item => item.title)).toEqual(['Отчёт'])
    expect(upcoming(data, 'task', '2026-02-01')[0].day).toBe('2026-02-28')
    const done = saveEntry(data, setStatus(entry, 'done'), true)
    expect(dayEntries(done, '2026-02-28')).toEqual([])
    expect(upcoming(done, 'task', '2026-02-01')).toEqual([])
  })

  it('возвращает только наступившие и ещё не скрытые напоминания', () => {
    const data = saveEntry(seed(), { ...task('Запись'), blocks: [{ id: 'notice', type: 'reminder', value: '2026-02-28T10:00' }] })
    expect(dueReminders(data, new Date('2026-02-28T09:59').getTime(), [])).toEqual([])
    const due = dueReminders(data, new Date('2026-02-28T10:00').getTime(), [])
    expect(due).toHaveLength(1)
    expect(dueReminders(data, new Date('2026-02-28T10:00').getTime(), [due[0].key])).toEqual([])
  })

  it('считает копейки точно и не включает запланированные платежи в баланс', () => {
    const payment = (data: Data, title: string, amount: string, direction: 'income' | 'expense', done: boolean) => {
      const entry = { ...createCard('payment', 'finance'), title, direction, category: 'Дом',
        blocks: [{ id: 'cost', type: 'amount', value: amount }, date('2026-11-23')] } satisfies ReturnType<typeof createCard>
      return saveEntry(data, setStatus(entry, done ? 'done' : 'planned'))
    }
    let data = payment(seed(), 'Доход', '9007199254740994.01', 'income', true)
    data = payment(data, '=Расход', '9007199254740993.99', 'expense', true)
    data = payment(data, 'Запланировано', '5.20', 'expense', false)
    const items = operations(data)
    expect(financeTotals(items)).toMatchObject({ balance: 2n, planned: 520n })
    expect(financeCsv(items)).toContain("\"'=Расход\"")
  })
})
