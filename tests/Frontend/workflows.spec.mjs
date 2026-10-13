import { test, expect } from '@playwright/test';
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';

let fixtures;
test.beforeAll(async () => {
    fixtures = JSON.parse(await readFile(join(process.env.FRONTEND_TEST_RUN_DIR, 'fixtures.json'), 'utf8'));
});

async function openReady(page, path)
{
    await page.goto(path);
    // Local timestamp formatting occurs after the interactive circuit renders.
    // Waiting for visible output avoids clicking the prerendered form before hydration.
    await expect(page.locator('time').first()).toContainText('(UTC)');
}

test('long saved review keeps actions reachable and returns keyboard focus', async ({ page }) => {
    await openReady(page, `/data/location-management/scans/${fixtures.scanId}`);
    for (const index of [0, 50])
    {
        await page.getByRole('checkbox', { name: `Select asset ${fixtures.assetIds[index]}`, exact: true }).check();
    }
    const bar = page.getByRole('region', { name: 'Selection actions', exact: true });
    await expect(bar).toContainText('2 selected');
    const box = await bar.boundingBox();
    expect(box.y).toBeGreaterThanOrEqual(0);
    expect(box.y + box.height).toBeLessThanOrEqual(page.viewportSize().height);
    await bar.getByRole('button', { name: 'Review changes', exact: true }).press('Enter');
    await expect(page.getByRole('heading', { name: 'Review 2 selected changes', exact: true })).toBeFocused();
    await expect(bar).toBeHidden();
    const confirm = page.getByRole('button', { name: 'Confirm Apply 2 Changes', exact: true });
    await expect(confirm).toBeDisabled();
    const acknowledgment = page.getByRole('checkbox', { name: 'I have reviewed these changes and understand that they update Immich.', exact: true });
    await acknowledgment.check();
    await expect(confirm).toBeEnabled();
    await acknowledgment.uncheck();
    await expect(confirm).toBeDisabled();
    await page.getByRole('button', { name: 'Back to Selection', exact: true }).press('Enter');
    await expect(bar.getByRole('button', { name: 'Review changes', exact: true })).toBeFocused();
    await bar.getByRole('button', { name: 'Clear', exact: true }).click();
    await expect(bar).toBeHidden();
    await expect(page.getByRole('heading', { name: 'Apply Selected Changes', exact: true })).toBeFocused();
});

test('refreshing an open review retains selection and restores its actions', async ({ page }) => {
    await openReady(page, `/data/location-management/scans/${fixtures.scanId}`);
    await page.getByRole('checkbox', { name: `Select asset ${fixtures.assetIds[0]}`, exact: true }).check();
    await page.getByRole('button', { name: 'Review 1 Selected Changes', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Review 1 selected changes', exact: true })).toBeFocused();
    await page.getByRole('button', { name: 'Back to Selection', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Review 1 Selected Changes', exact: true })).toBeFocused();
    await page.getByRole('button', { name: 'Review changes', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Review 1 selected changes', exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Refresh saved results', exact: true }).click();
    await expect(page.getByRole('region', { name: 'Selection actions', exact: true })).toContainText('1 selected');
});

test('continued scans use the same review and focus behavior', async ({ page }) => {
    await page.goto(`/data/location-management/scans/${fixtures.scanId}/continue`);
    await expect(page.getByRole('button', { name: 'Continue Saved Scan', exact: true })).toBeEnabled();
    await page.getByRole('checkbox', { name: `Select asset ${fixtures.assetIds[0]}`, exact: true }).check();
    await page.getByRole('button', { name: 'Review changes', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Review 1 selected changes', exact: true })).toBeFocused();
    await page.getByRole('button', { name: 'Back to Selection', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Review changes', exact: true })).toBeFocused();
});

test('undo review preserves the confirmation guard and focus return', async ({ page }) => {
    await openReady(page, `/data/location-management/history/${fixtures.operationId}`);
    await page.getByRole('checkbox', { name: `Select asset ${fixtures.undoAssetId} for undo`, exact: true }).check();
    await page.getByRole('button', { name: 'Review undo', exact: true }).press('Enter');
    await expect(page.getByRole('heading', { name: 'Undo 1 selected changes', exact: true })).toBeFocused();
    await expect(page.getByRole('button', { name: 'Confirm', exact: true })).toBeDisabled();
    await page.getByRole('button', { name: 'Keep Results', exact: true }).press('Enter');
    await expect(page.getByRole('button', { name: 'Review undo', exact: true })).toBeFocused();
    await page.getByRole('button', { name: 'Clear', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Operation results — page 1', exact: true })).toBeFocused();
});

test('pending apply and undo reviews return focus to their own triggers', async ({ page }) => {
    for (const [id, button, heading] of [
        [fixtures.operationId, 'Resume Pending Apply', 'Resume 1 pending apply writes'],
        [fixtures.pendingUndoId, 'Resume Pending Undo', 'Resume 1 pending undo writes']
    ])
    {
        await openReady(page, `/data/location-management/history/${id}`);
        await page.getByRole('button', { name: button, exact: true }).click();
        await expect(page.getByRole('heading', { name: heading, exact: true })).toBeFocused();
        await expect(page.getByRole('button', { name: 'Confirm', exact: true })).toBeDisabled();
        await page.getByRole('button', { name: 'Keep Results', exact: true }).click();
        await expect(page.getByRole('button', { name: button, exact: true })).toBeFocused();
    }
});

test('history filters find older matches before pagination and reset the page', async ({ page }) => {
    await openReady(page, '/data/location-management/scans');
    await expect(page.getByRole('link', { name: 'Continue scan', exact: true })).toHaveCount(0);
    await page.getByRole('combobox', { name: 'Show scans', exact: true }).selectOption({ label: 'Can continue' });
    await expect(page.getByRole('link', { name: 'Continue scan', exact: true })).toHaveCount(25);
    await page.getByRole('button', { name: 'Next', exact: true }).click();
    await expect(page.getByText('Page 2', { exact: true })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Continue scan', exact: true })).toHaveCount(fixtures.continuableCount - 25);
    await expect(page.getByRole('button', { name: 'Next', exact: true })).toBeDisabled();
    await page.getByRole('combobox', { name: 'Show scans', exact: true }).selectOption({ label: 'Needs attention' });
    await expect(page.getByText('Page 1', { exact: true })).toBeVisible();
    await expect(page.getByRole('link', { name: 'View', exact: true })).toHaveCount(1);
    await expect(page.getByRole('link', { name: 'View', exact: true })).toHaveAttribute('href', `/data/location-management/scans/${fixtures.failedScanId}`);

    await openReady(page, '/data/location-management/history');
    await page.getByRole('combobox', { name: 'Show operations', exact: true }).selectOption({ label: 'Pending work' });
    await expect(page.getByRole('link', { name: 'View', exact: true })).toHaveCount(25);
    await page.getByRole('button', { name: 'Next', exact: true }).click();
    await expect(page.getByText('Page 2', { exact: true })).toBeVisible();
    await expect(page.getByRole('link', { name: 'View', exact: true })).toHaveCount(fixtures.pendingCount - 25);
    await page.getByRole('combobox', { name: 'Show operations', exact: true }).selectOption({ label: 'Needs review' });
    await expect(page.getByText('Page 1', { exact: true })).toBeVisible();
    await expect(page.getByRole('link', { name: 'View', exact: true })).toHaveCount(1);
    await expect(page.getByRole('link', { name: 'View', exact: true })).toHaveAttribute('href', `/data/location-management/history/${fixtures.operationId}`);
    const width = await page.evaluate(() => document.documentElement.scrollWidth);
    expect(width).toBeLessThanOrEqual(page.viewportSize().width);
});

test('connection recovery explains saved work after a temporary disconnect', async ({ page, context }) => {
    test.setTimeout(90000);
    await openReady(page, `/data/location-management/scans/${fixtures.scanId}`);
    await context.setOffline(true);
    try
    {
        const dialog = page.getByRole('dialog', { name: 'Connection status', exact: true });
        await expect(dialog).toBeVisible({ timeout: 45000 });
        await expect(dialog).toContainText('Reloading clears unsaved edits and live selections.');
        await expect(dialog).toContainText('Location Management → Saved Scans → Continue scan');
        await expect(dialog).toContainText('Change History');
        const box = await dialog.boundingBox();
        expect(box.x).toBeGreaterThanOrEqual(0);
        expect(box.y).toBeGreaterThanOrEqual(0);
        expect(box.y + box.height).toBeLessThanOrEqual(page.viewportSize().height);
    }
    finally
    {
        await context.setOffline(false);
    }
    await expect(page.getByRole('dialog', { name: 'Connection status', exact: true })).toBeHidden({ timeout: 45000 });
});
