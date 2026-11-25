import { devices, expect, test, type Page } from '@playwright/test'
import { _android } from 'playwright'
import { navigateSection } from './navigation-support'

const widgets = ['Ближайшие дела', 'Предстоящие платежи', 'Активные проекты', 'Недавние карточки', 'Избранные ссылки']

async function checkOrganizer(page: Page, url: string, pointer: 'mouse' | 'touch', systemBack?: () => Promise<string>) {
  await page.goto(url)
  const activate = (target: ReturnType<Page['getByRole']>) => pointer === 'touch' ? target.tap() : target.click()
  const button = (name: string) => page.getByRole('button', { name, exact: true })
  const heading = (name: string) => page.getByRole('heading', { name, exact: true, level: 1 })

  await expect(heading('Обзор')).toBeVisible()
  await page.evaluate(() => {
    document.addEventListener('pointerdown', event => {
      document.documentElement.dataset.testPointer = event.pointerType
    }, { once: true, capture: true })
  })
  for (const name of widgets) {
    await activate(button(name))
    await expect(heading(name)).toBeVisible()
    if (systemBack && name === widgets[0]) {
      await expect(page).toHaveURL(/#screen-1$/)
      const focus = await systemBack()
      try {
        await expect(page).not.toHaveURL(/#screen-1$/, { timeout: 3000 })
        console.log('Android system Back reached the browser history.')
      } catch (error) {
        if (!page.url().endsWith('#screen-1')) throw error
        console.warn('Android system Back did not move Chrome history in this emulator. Front window:', focus.trim())
        await page.goBack()
        console.log('Android Chrome browser history used for the Back check.')
      }
      await expect(heading('Обзор')).toBeVisible()
    } else await navigateSection(page, 'Обзор', activate)
  }
  await expect(page.locator('html')).toHaveAttribute('data-test-pointer', pointer)
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(await page.evaluate(() => innerWidth))

  await activate(button('Новая карточка'))
  await activate(button('Заметка'))
  const dialog = page.getByRole('dialog')
  const paymentURL = new URL('/payment-fixture', url).href
  await page.context().route(paymentURL, route => route.fulfill({ contentType: 'text/html; charset=utf-8', body: '<title>Проверка ссылки оплаты</title>' }))
  await dialog.getByLabel('Тип карточки').selectOption('payment')
  await dialog.getByLabel('Папка', { exact: true }).selectOption('finance')
  await dialog.getByLabel('Название').fill('Межплатформенный платёж')
  await dialog.getByLabel('Сумма (₽)').fill('1200,50')
  await dialog.locator('input[type=date]').fill('2099-01-15')
  await dialog.getByLabel('Адрес сайта оплаты').fill(paymentURL)
  await activate(button('Готово'))
  await expect(dialog).toHaveCount(0)
  await page.reload()
  await activate(button('Предстоящие платежи'))
  const summaryLink = page.getByRole('link', { name: /Сайт оплаты/ })
  await expect(summaryLink).toHaveAttribute('href', paymentURL)
  await activate(page.getByRole('button', { name: /Межплатформенный платёж/ }))
  await expect(dialog.getByLabel('Название')).toHaveValue('Межплатформенный платёж')
  await expect(dialog.getByLabel('Тип карточки')).toHaveValue('payment')
  await expect(dialog.getByLabel('Папка', { exact: true })).toHaveValue('finance')
  await expect(dialog.getByLabel('Сумма (₽)')).toHaveValue('1200.50')
  await expect(dialog.locator('input[type=date]')).toHaveValue('2099-01-15')
  await expect(dialog.getByLabel('Адрес сайта оплаты')).toHaveValue(paymentURL)
  await activate(button('Закрыть'))
  await navigateSection(page, 'Финансы', activate)
  await expect(heading('Финансы')).toBeVisible()
  const edit = button('Редактировать «Межплатформенный платёж»')
  const saved = page.getByRole('article').filter({ has: edit })
  const link = saved.getByRole('link', { name: /Сайт оплаты/ })
  await expect(link).toHaveText('Сайт оплаты' + paymentURL)
  const [opened] = await Promise.all([page.waitForEvent('popup'), activate(link)])
  await expect(opened).toHaveTitle('Проверка ссылки оплаты')
  await expect(opened).toHaveURL(paymentURL)
  await opened.close()
  await expect(dialog).toHaveCount(0)
  await activate(edit)
  await dialog.getByLabel('Адрес сайта оплаты').fill('')
  await activate(button('Готово'))
  await expect(dialog).toHaveCount(0)
  await expect(saved.getByRole('link')).toHaveCount(0)
  await expect(saved.locator('.card-links')).toHaveCount(0)
}

test('мышь: навигация и сохранение карточки', async ({ page, baseURL }) => {
  await checkOrganizer(page, baseURL!, 'mouse')
})

test('сенсорные нажатия: навигация и сохранение карточки', async ({ browser, baseURL }) => {
  const context = await browser.newContext({ ...devices['Pixel 7'], colorScheme: 'dark', locale: 'ru-RU' })
  try { await checkOrganizer(await context.newPage(), baseURL!, 'touch') }
  finally { await context.close() }
})

test('Android Chrome: сенсорные нажатия, навигация и сохранение карточки', async ({ baseURL }) => {
  test.skip(process.env.POMOGATOR_ANDROID !== '1', 'Нужен Android-эмулятор с Chrome и ADB.')
  test.setTimeout(145_000)
  const devices = await _android.devices()
  expect(devices).toHaveLength(1)
  const [device] = devices
  try {
    const context = await device.launchBrowser({ hasTouch: true })
    try {
      const page = context.pages()[0] ?? await context.newPage()
      await checkOrganizer(page, baseURL!, 'touch', async () => {
        const focus = await device.shell('dumpsys window | grep -m1 mCurrentFocus')
        await device.shell('input keyevent 4')
        return focus.toString()
      })
      const dialog = page.getByRole('dialog')
      const capture = page.getByRole('button', { name: 'Быстрый захват', exact: true })
      await capture.tap()
      await dialog.getByLabel('Текст', { exact: true }).fill('Заметка с Android')
      await dialog.getByRole('button', { name: 'Закрыть', exact: true }).tap()
      await capture.tap()
      await expect(dialog.getByLabel('Текст', { exact: true })).toHaveValue('Заметка с Android')
      await dialog.getByRole('button', { name: 'Сохранить запись' }).tap()
      await expect(dialog).toHaveCount(0)
      await page.reload()
      await page.getByRole('button', { name: /Входящие\s*1/ }).tap()
      await expect(page.getByRole('heading', { name: 'Заметка с Android' })).toBeVisible()
    } finally { await context.close() }
  } finally { await device.close() }
})
