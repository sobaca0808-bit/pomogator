import { expect, test, type Page } from '@playwright/test'
import { readFile } from 'node:fs/promises'
import { join } from 'node:path'
import { exportBackup } from '../src/backup'
import { createCard, type Block, type Kind } from '../src/cards'
import { saveEntry, savePlace, seed, type Data } from '../src/model'

const nav = (page: Page, name: string) => page.getByRole('navigation', { name: 'Разделы', exact: true }).getByRole('button', { name, exact: true })
const addBlock = async (page: Page, value: string) => {
  await page.getByLabel('Тип блока').selectOption(value)
  await page.getByRole('button', { name: 'Добавить блок' }).click()
}
async function importText(page: Page, json: string) {
  await page.locator('.sidebar input[type=file]').setInputFiles({
    name: 'backup.json', mimeType: 'application/json', buffer: Buffer.from(json),
  })
}

test('вложенные папки, автосохранение, вложение, экспорт, импорт и отмена удаления', async ({ page }) => {
  await page.goto('/')
  await nav(page, 'Документы').click()
  for (const title of ['Работа', 'Договоры']) {
    await page.getByRole('button', { name: 'Папка', exact: true }).click()
    await page.getByLabel('Название').fill(title)
    await page.getByRole('button', { name: 'Сохранить', exact: true }).click()
    await page.getByRole('button', { name: new RegExp(title + ' 0 пап') }).click()
  }
  await page.getByRole('button', { name: 'Новая карточка' }).click()
  await page.getByLabel('Название').fill('Договор поставки')
  await page.getByLabel('Выбрать файл').setInputFiles({
    name: 'delivery.txt', mimeType: 'text/plain', buffer: Buffer.from('Точное содержимое вложения'),
  })
  await page.getByLabel('Описание', { exact: true }).fill('Для проверки')
  await addBlock(page, 'link')
  await page.getByLabel('Адрес ссылки').fill('https://example.org/contract')
  await page.getByLabel('Подпись ссылки').fill('Электронная версия')
  await addBlock(page, 'tag')
  await page.getByRole('textbox', { name: 'Тег', exact: true }).fill('Поставщик')
  await page.getByRole('button', { name: 'Поднять блок «Тег»' }).click()
  await expect(page.getByRole('status').filter({ hasText: 'Сохранено в этом браузере' })).toBeVisible()
  await page.reload()
  await page.getByRole('searchbox').fill('ПОСТАВЩИК')
  await page.getByRole('button', { name: /Договор поставки/ }).click()
  await expect(page.getByLabel('Название')).toHaveValue('Договор поставки')
  await page.getByRole('dialog').getByRole('button', { name: 'Просмотреть текст' }).click()
  await expect(page.getByText('Точное содержимое вложения')).toBeVisible()
  await page.getByRole('button', { name: 'Готово', exact: true }).click()
  await expect(page.getByRole('link', { name: 'Электронная версия' })).toHaveAttribute('href', 'https://example.org/contract')

  const downloadPromise = page.waitForEvent('download')
  await page.getByRole('button', { name: 'Экспорт JSON' }).click()
  const backup = await readFile((await (await downloadPromise).path())!, 'utf8')
  expect(JSON.parse(backup).entries[0].blocks.find((block: Block) => block.type === 'file').file.base64)
    .toBe(Buffer.from('Точное содержимое вложения').toString('base64'))
  await page.getByRole('button', { name: 'Удалить «Договор поставки»' }).click()
  await page.getByRole('button', { name: 'Подтвердить' }).click()
  await expect(page.getByRole('heading', { name: 'Договор поставки' })).toHaveCount(0)
  await page.getByRole('button', { name: 'Отменить удаление' }).click()
  await expect(page.getByRole('heading', { name: 'Договор поставки' })).toBeVisible()

  await importText(page, '{"version":2,"places":[],"entries":[{}]}')
  await expect(page.getByRole('alert')).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Договор поставки' })).toBeVisible()
  await page.getByRole('button', { name: 'Удалить «Договор поставки»' }).click()
  await page.getByRole('button', { name: 'Подтвердить' }).click()
  await expect(page.getByRole('dialog')).toHaveCount(0)
  await importText(page, backup)
  await expect(page.getByRole('heading', { name: 'Заменить локальные данные?' })).toBeVisible()
  await page.getByRole('button', { name: 'Отмена', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Договор поставки' })).toHaveCount(0)
  await importText(page, backup)
  await page.getByRole('button', { name: 'Подтвердить' }).click()
  await expect(page.getByRole('heading', { name: 'Обзор' })).toBeVisible()
  await page.getByRole('button', { name: /Договор поставки/ }).click()
  const filePromise = page.waitForEvent('download')
  await page.getByRole('button', { name: 'Скачать delivery.txt' }).click()
  const file = await filePromise
  expect(file.suggestedFilename()).toBe('delivery.txt')
  expect(await readFile((await file.path())!, 'utf8')).toBe('Точное содержимое вложения')
})

test('настройки виджетов, календарь и клавиатура на телефоне', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Обзор' })).toBeVisible()
  await page.getByRole('button', { name: 'Виджеты' }).click()
  const widget = page.getByRole('checkbox', { name: 'Сегодня' })
  await widget.click()
  await expect(widget).not.toBeChecked()
  const move = page.getByRole('button', { name: 'Поднять «Ближайшие дела»' })
  await move.click()
  await expect(page.getByRole('dialog').getByRole('checkbox').first()).toHaveAccessibleName('Ближайшие дела')
  await page.getByRole('button', { name: 'Готово' }).click()
  await page.reload()
  await expect(page.getByRole('heading', { name: 'Сегодня', exact: true })).toHaveCount(0)
  await expect(page.locator('.overview-grid h2').first()).toHaveText('Ближайшие дела')
  await page.keyboard.press('Control+k')
  await expect(page.getByRole('searchbox')).toBeFocused()
  await nav(page, 'Календарь').click()
  await expect(page.getByRole('heading', { name: 'Календарь' })).toBeVisible()
  await page.getByRole('button', { name: 'Карточка на эту дату' }).click()
  await page.getByRole('button', { name: 'Задача', exact: true }).click()
  await page.getByLabel('Название').fill('Встреча в календаре')
  await page.getByRole('button', { name: 'Готово' }).click()
  await expect(page.getByRole('button', { name: /Встреча в календаре/ })).toBeVisible()
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390)
})

const date = (value: string): Block => ({ id: 'date', type: 'date', value })
const time = (value: string): Block => ({ id: 'time', type: 'time', value })
const status = (value: 'planned' | 'active' | 'done'): Block => ({ id: 'status', type: 'status', value })
const link = (value: string, label: string): Block => ({ id: 'link', type: 'link', value, label })
function demo() {
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

test('основные экраны на компьютере и телефоне', async ({ page }, testInfo) => {
  const screenshots = process.env.POMOGATOR_SCREENSHOTS || testInfo.outputDir
  await page.clock.setFixedTime(new Date('2026-11-23T10:00:00Z'))
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.goto('/')
  await importText(page, await exportBackup(demo()))
  await page.getByRole('button', { name: 'Подтвердить' }).click()
  await expect(page.getByRole('heading', { name: 'Избранные ссылки' })).toBeVisible()
  await expect(page.getByText('Подготовить материалы встречи').first()).toBeVisible()
  await page.screenshot({ path: join(screenshots, '01-overview-desktop.png'), animations: 'disabled' })
  await nav(page, 'Календарь').click()
  await expect(page.getByRole('button', { name: /Подготовить материалы встречи/ })).toBeVisible()
  await page.screenshot({ path: join(screenshots, '02-calendar-desktop.png'), animations: 'disabled' })
  await nav(page, 'Мои проекты').click()
  await page.getByRole('button', { name: 'Редактировать «Обновить рабочее место»' }).click()
  await expect(page.getByLabel('Название')).toHaveValue('Обновить рабочее место')
  await page.screenshot({ path: join(screenshots, '03-card-desktop.png'), animations: 'disabled' })
  await page.keyboard.press('Escape')
  await expect(page.getByRole('dialog')).toHaveCount(0)
  await page.setViewportSize({ width: 390, height: 844 })
  await nav(page, 'Обзор').click()
  await expect(page.getByRole('heading', { name: 'Обзор' })).toBeVisible()
  await page.screenshot({ path: join(screenshots, '04-overview-mobile.png'), fullPage: true, animations: 'disabled' })
})

test('устаревший редактор не перезаписывает заменённую в другой вкладке базу', async ({ context, page }) => {
  await page.goto('/')
  const original = saveEntry(seed(), { ...createCard('note', 'notes'), title: 'Исходная заметка' })
  await importText(page, await exportBackup(original))
  await page.getByRole('button', { name: 'Подтвердить' }).click()
  await page.getByRole('button', { name: /Исходная заметка/ }).click()
  await expect(page.getByLabel('Название')).toHaveValue('Исходная заметка')

  const second = await context.newPage()
  await second.goto('/')
  const replacement = { ...original, entries: [{ ...original.entries[0], title: 'Восстановленная заметка' }] }
  await importText(second, await exportBackup(replacement))
  await second.getByRole('button', { name: 'Подтвердить' }).click()
  await expect(second.getByRole('button', { name: /Восстановленная заметка/ })).toBeVisible()

  await page.getByLabel('Название').fill('Правка из старого окна')
  await expect(page.getByRole('alert')).toHaveText('База заменена в другой вкладке. Откройте карточку заново.')
  await page.reload()
  await expect(page.getByRole('button', { name: /Восстановленная заметка/ })).toBeVisible()
  await expect(page.getByRole('button', { name: /Правка из старого окна/ })).toHaveCount(0)
})
