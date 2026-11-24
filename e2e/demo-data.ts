import { createCard, type Block, type Kind } from '../src/cards'
import { saveEntry, savePlace, seed, type Data } from '../src/model'

const date = (value: string): Block => ({ id: 'date', type: 'date', value })
const time = (value: string): Block => ({ id: 'time', type: 'time', value })
const status = (value: 'planned' | 'active' | 'done'): Block => ({ id: 'status', type: 'status', value })
const link = (value: string, label: string): Block => ({ id: 'link', type: 'link', value, label })
export function demo() {
  const demoCard = (data: Data, kind: Kind, parent: string, title: string, blocks: Block[], options = {}) =>
    saveEntry(data, { ...createCard(kind, parent), title, blocks, ...options })
  let data = savePlace(seed(), { id: 'home', parent: 'projects', title: 'Дом и семья', icon: 'folder' })
  data = demoCard(data, 'project', 'projects', 'Обновить рабочее место', [
    status('active'), date('2026-12-04'), { id: 'check', type: 'checklist', items: [
      { text: 'Снять размеры стола', done: true }, { text: 'Выбрать светильник', done: true }, { text: 'Собрать заказ', done: false },
    ] }, { id: 'sum', type: 'amount', value: '28000' },
  ])
  data = demoCard(data, 'project', 'projects', 'План поездки в Казань', [status('active'), date('2026-12-11')])
  data = demoCard(data, 'task', 'tasks', 'Подготовить материалы встречи', [date('2026-11-23'), time('14:00'), status('active')], { priority: 'high' })
  data = demoCard(data, 'task', 'tasks', 'Забрать заказ', [date('2026-11-23'), time('18:30'), status('planned')])
  data = demoCard(data, 'task', 'tasks', 'Проверить смету на декабрь', [date('2026-11-25'), time('11:00'), status('planned')])
  data = demoCard(data, 'task', 'tasks', 'Обновить список дел', [date('2026-11-27'), status('planned')], { repeat: 'weekly' })
  data = demoCard(data, 'payment', 'finance', 'Домашний интернет', [date('2026-11-24'), { id: 'sum', type: 'amount', value: '850' }, status('planned')], { repeat: 'monthly', category: 'Связь' })
  data = demoCard(data, 'payment', 'finance', 'Коммунальные услуги', [date('2026-11-28'), { id: 'sum', type: 'amount', value: '4720' }, status('planned')], { repeat: 'monthly', category: 'Жильё' })
  data = demoCard(data, 'note', 'notes', 'Идеи для выходных', [{ id: 'text', type: 'text', value: 'Музей, прогулка и ужин с друзьями.', format: 'paragraph' }])
  data = demoCard(data, 'link', 'services', 'Расписание поездов', [link('https://www.rzd.ru/', 'Открыть расписание')], { favorite: true })
  return demoCard(data, 'link', 'services', 'Репозиторий ПОМОГАТОРА', [link('https://github.com/sobaca0808-bit/pomogator', 'GitHub')], { favorite: true })
}
