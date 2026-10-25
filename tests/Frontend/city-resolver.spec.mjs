import { test, expect } from '@playwright/test';
import { readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';

test('city resolver preserves inheritance and independent explicit tie-breaks after reload', async ({ page }) => {
    const path = join(process.env.FRONTEND_TEST_RUN_DIR, 'config', 'settings.json');
    const config = JSON.parse(await readFile(path, 'utf8'));
    config.processing.cityResolver = { defaultProfile: { preferredSubtypes: [], tieBreakMode: '' }, countryOverrides: {} };
    await writeFile(path, JSON.stringify(config));
    await openEditor(page);
    await page.getByRole('checkbox', { name: 'Customize the global default profile' }).check();
    const global = page.getByRole('combobox', { name: 'Tie-break mode', exact: true }).first();
    await expect(global).toHaveValue('');
    await global.selectOption('largest-area');
    await page.getByLabel('Country', { exact: true }).fill('FRA');
    await page.getByRole('button', { name: 'Add Country Override', exact: true }).click();
    const country = page.getByRole('combobox', { name: 'Tie-break mode', exact: true }).nth(1);
    await expect(country).toHaveValue('');
    await expect(page.getByText('Inherit the default place-type order.', { exact: true })).toHaveCount(2);
    // Explicit tighter matching must save even with no place-type override.
    await country.selectOption('smallest-area');
    const save = page.getByRole('button', { name: 'Save changes', exact: true });
    await save.click();
    await expect(save).toBeDisabled();
    await openEditor(page);
    const disclosure = page.locator('details.resolver-disclosure');
    await disclosure.locator('summary').click();
    await expect(global).toHaveValue('largest-area');
    await expect(country).toHaveValue('smallest-area');
    await country.selectOption('');
    await save.click();
    await expect(save).toBeDisabled();
    await openEditor(page);
    await disclosure.locator('summary').click();
    await expect(country).toHaveValue('');
    await expect(disclosure).toContainText('broader');
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(page.viewportSize().width);
});

async function openEditor(page)
{
    await page.goto('/data/location-management/scans');
    await expect(page.locator('time').first()).toContainText('(UTC)');
    const navigation = page.getByRole('button', { name: 'Open Navigation', exact: true });
    if (await navigation.isVisible())
    {
        await navigation.click();
    }
    await page.getByRole('link', { name: 'City Resolver', exact: true }).click();
}
