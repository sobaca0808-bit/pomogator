import { devices, expect, test, type Page } from '@playwright/test'
import { _android } from 'playwright'

const widgets = ['Ближайшие дела', 'Предстоящие платежи', 'Активные проекты', 'Недавние карточки', 'Избранные ссылки']

async function checkOrganizer(page: Page, url: string, pointer: 'mouse' | 'touch', systemBack?: () => Promise<unknown>) {
  await page.goto(url)
  const activate = (target: ReturnType<Page['getByRole']>) => pointer === 'touch' ? target.tap() : target.click()
  const button = (name: string) => page.getByRole('button', { name, exact: true })
  const heading = (name: string) => page.getByRole('heading', { name, exact: true, level: 1 })
  const nav = (name: string) => page.getByRole('navigation', { name: 'Разделы' }).getByRole('button', { name, exact: true })

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
      const state = () => page.evaluate(() => ({
        hash: location.hash, index: history.state?.index, activation: navigator.userActivation.hasBeenActive,
      }))
      const before = await state()
      await systemBack()
      await expect(heading('Обзор')).toBeVisible({ timeout: 10_000 }).catch(async error => {
        console.error('Android back history', { before, after: await state() })
        throw error
      })
    } else await activate(nav('Обзор'))
  }
  await expect(page.locator('html')).toHaveAttribute('data-test-pointer', pointer)
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(await page.evaluate(() => innerWidth))

  await activate(button('Новая карточка'))
  await activate(button('Заметка'))
  const dialog = page.getByRole('dialog')
  await dialog.getByLabel('Тип карточки').selectOption('payment')
  await dialog.getByLabel('Папка', { exact: true }).selectOption('finance')
  await dialog.getByLabel('Название').fill('Межплатформенный платёж')
  await dialog.getByLabel('Сумма (₽)').fill('1200,50')
  await dialog.locator('input[type=date]').fill('2099-01-15')
  await activate(button('Готово'))
  await expect(dialog).toHaveCount(0)
  await page.reload()
  await activate(button('Предстоящие платежи'))
  await activate(page.getByRole('button', { name: /Межплатформенный платёж/ }))
  await expect(dialog.getByLabel('Название')).toHaveValue('Межплатформенный платёж')
  await expect(dialog.getByLabel('Тип карточки')).toHaveValue('payment')
  await expect(dialog.getByLabel('Папка', { exact: true })).toHaveValue('finance')
  await expect(dialog.getByLabel('Сумма (₽)')).toHaveValue('1200.50')
  await expect(dialog.locator('input[type=date]')).toHaveValue('2099-01-15')
  await activate(button('Закрыть'))
  await activate(nav('Финансы'))
  await expect(heading('Финансы')).toBeVisible()
  await expect(button('Редактировать «Межплатформенный платёж»')).toBeVisible()
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
  test.setTimeout(90_000)
  const devices = await _android.devices()
  expect(devices).toHaveLength(1)
  const [device] = devices
  try {
    const context = await device.launchBrowser({ hasTouch: true })
    try {
      const page = context.pages()[0] ?? await context.newPage()
      await checkOrganizer(page, baseURL!, 'touch', () => device.shell('input keyevent KEYCODE_BACK'))
    } finally { await context.close() }
  } finally { await device.close() }
})
