import type { Locator, Page } from '@playwright/test'

export async function navigateSection(page: Page, name: string, activate: (target: Locator) => Promise<void> = target => target.click()) {
  const direct = page.getByRole('navigation', { name: 'Главное меню' }).getByRole('button', { name, exact: true })
  if (await direct.isVisible()) {
    await activate(direct)
    return
  }
  const menu = page.locator('.navigation-menu')
  if (await menu.getAttribute('open') === null) await activate(menu.locator('summary'))
  await activate(menu.getByRole('button', { name, exact: true }))
}
