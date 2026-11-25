import { expect, test } from 'vitest'
import { createCard } from './cards'
import { moveEntry, seed } from './model'

test('перенос старой карточки сохраняет тип, категорию, историю и идентификатор', () => {
  const entry = { ...createCard('payment', 'finance'), kind: undefined, blocks: undefined,
    title: 'Счёт', text: 'Договор', amount: '123.45', category: 'Связь', favorite: true,
    created: '2020-01-01', history: Array.from({ length: 30 }, (_, i) => ({ at: String(i), note: 'Правка' })) }
  const data = { ...seed(), entries: [entry] }
  const moved = moveEntry(data, entry, 'notes')
  expect(moved.entries).toHaveLength(1)
  expect(moved.entries[0]).toMatchObject({ id: entry.id, parent: 'notes', kind: 'payment', created: entry.created,
    text: entry.text, amount: entry.amount, category: entry.category, favorite: true })
  expect(moved.entries[0].history?.slice(1)).toEqual(entry.history)
  expect(() => moveEntry(moved, entry, 'finance')).toThrow('другой вкладке')
  expect(() => moveEntry(data, entry, 'missing')).toThrow()
})
