import { expect, test } from '@playwright/test'
import { join } from 'node:path'
import { navigateSection } from './navigation-support'

for (const width of [390, 1366]) {
  test(`плитки, перемещение и отправка с вложением · ${width}`, async ({ page }, info) => {
    await page.setViewportSize({ width, height: 844 })
    await page.goto('/')
    const button = (name: string | RegExp) => page.getByRole('button', { name, exact: typeof name === 'string' })
    const tiles = page.locator('.overview-places > article')
    await expect(tiles.first()).toContainText('Входящие')
    await expect(tiles.nth(1)).toContainText('Мои проекты')
    await expect(page.getByRole('searchbox')).toHaveCount(0)
    const a = (await tiles.first().boundingBox())!
    const b = (await tiles.nth(1).boundingBox())!
    expect(a.height).toBe(b.height)
    expect(Math.abs(a.width - b.width)).toBeLessThan(1)
    await page.screenshot({ path: join(process.env.POMOGATOR_SCREENSHOTS || info.outputDir, `home-${width}.png`), fullPage: true })
    await button('Быстрый захват').click()
    const dialog = page.getByRole('dialog')
    await dialog.getByLabel('Текст', { exact: true }).fill('Смета\nНа май')
    await dialog.locator('input[type=file]').setInputFiles({ name: 'смета.txt', mimeType: 'text/plain', buffer: Buffer.from('123') })
    await button('Сохранить запись').click()
    await button(/Входящие\s*1/).click()
    await button('Избранное «Смета»').click()
    await button('Переместить').click()
    await dialog.getByLabel('Куда переместить').selectOption('finance')
    await dialog.getByRole('button', { name: 'Переместить' }).click()
    await expect(page.getByRole('article').filter({ hasText: 'Смета' })).toHaveCount(0)
    await page.reload()
    await navigateSection(page, 'Финансы')
    await expect(button('Избранное «Смета»')).toHaveAttribute('aria-pressed', 'true')
    await expect(page.locator('.entry-title small')).toHaveText('Заметка')
    await page.evaluate(() => {
      Object.defineProperty(navigator, 'share', { configurable: true, value: async (payload: ShareData) => {
        document.body.dataset.shared = [payload.text, payload.files?.[0].name, await payload.files?.[0].text()].join('|')
      } })
      Object.defineProperty(navigator, 'canShare', { configurable: true, value: () => true })
    })
    await button('Поделиться').click()
    await expect(page.locator('body')).toHaveAttribute('data-shared', /На май\|смета.txt\|123/)
    await page.evaluate(() => Object.defineProperty(navigator, 'share', { value: undefined }))
    await button('Поделиться').click()
    await expect(dialog.getByRole('textbox')).toHaveValue(/На май/)
    await expect(dialog.getByRole('button', { name: 'Скачать смета.txt' })).toBeVisible()
  })
}
