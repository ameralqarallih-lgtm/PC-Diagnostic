import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import type { ScreenDriverInfo } from '@/lib/api/types';
import { confirmIt, mockApi, renderApp } from '@/test/app';
import { statusResponse } from '@/test/fixtures';

const builtIn: ScreenDriverInfo = { id: 'lianli-turzx', name: 'Lian Li screens', version: '1.0', publisher: 'PC Diagnostic', builtIn: true, sha256: null, approved: true, loaded: true, problem: null, models: ['Lian Li Universal Screen 8.8″'], capabilities: ['screen'], sensors: [] };
const probe: ScreenDriverInfo = { id: 'aquarium', name: 'Aquarium probe', version: '1.0', publisher: 'Me', builtIn: false, sha256: 'b'.repeat(64), approved: false, loaded: false, problem: 'Waiting for your approval.', models: [], capabilities: ['sensors'], sensors: ['Water'] };

describe('Drivers page', () => {
  it('imports a package, then approves and removes it, each asked first', async () => {
    let list: ScreenDriverInfo[] = [builtIn];
    const uploads: string[] = [];
    const fetchMock = mockApi({
      'GET /api/v1/status': () => statusResponse(),
      'GET /api/v1/drivers': () => list,
      'GET /api/v1/drivers/windows': () => ({ devices: [], packages: [], message: null }),
      'POST /api/v1/drivers/import': (_url, init) => {
        uploads.push((init!.headers as Record<string, string>)['Content-Type']!);
        list = [builtIn, probe];
        return list;
      },
      'POST /api/v1/drivers/aquarium/approve': () => {
        list = [builtIn, { ...probe, approved: true, loaded: true, problem: null }];
        return list;
      },
      'DELETE /api/v1/drivers/aquarium': () => {
        list = [builtIn];
        return list;
      },
    });
    renderApp('/drivers');

    const packages = await screen.findByRole('list', { name: 'Driver packages' });
    expect(within(packages).getByText('Built in')).toBeInTheDocument();
    // Built-in packages cannot be removed.
    expect(within(packages).queryByRole('button', { name: 'Remove Lian Li screens' })).toBeNull();

    await userEvent.upload(screen.getByLabelText('Driver package (zip)'), new File(['PK'], 'aquarium.zip', { type: 'application/zip' }));
    const item = await within(packages).findByRole('listitem', { name: 'Aquarium probe' });
    expect(uploads).toEqual(['application/zip']);
    expect(within(item).getByText('Sensors')).toBeInTheDocument();
    expect(within(item).getByText('Waiting for your approval.')).toBeInTheDocument();

    await userEvent.click(within(item).getByRole('button', { name: 'Approve' }));
    expect(await screen.findByRole('dialog', { name: 'Approve Aquarium probe?' })).toHaveTextContent('Its sensors are read every few seconds');
    await confirmIt();
    expect(await within(item).findByText('Approved')).toBeInTheDocument();

    await userEvent.click(within(item).getByRole('button', { name: 'Remove Aquarium probe' }));
    await confirmIt();
    await waitFor(() => expect(within(packages).queryByRole('listitem', { name: 'Aquarium probe' })).toBeNull());
    expect(fetchMock.mock.calls.some(([u, i]) => String(u).endsWith('/api/v1/drivers/aquarium') && i?.method === 'DELETE')).toBe(true);
  });

  it('lists devices without a driver, and installs an imported driver for one after asking (and on the PC)', async () => {
    const device = { instanceId: 'USB\\VID_1A86&PID_7523\\5&1', name: 'USB Serial', class: null, problemCode: 28, problem: 'Windows has no driver for it', hardwareIds: ['USB\\VID_1A86&PID_7523'], compatibleIds: [] };
    const driver = { id: 'ch341ser-1a2b3c4d', inf: 'CH341SER.INF', provider: 'wch.cn', version: '3.9.2024.0621', date: '06/21/2024', class: 'Ports', signature: 'Signed', signer: 'Microsoft Windows Hardware Compatibility Publisher', hardwareIds: ['USB\\VID_1A86&PID_7523'], matches: [device.instanceId], installed: null, problem: null };
    const installs: unknown[] = [];
    mockApi({
      'GET /api/v1/status': () => statusResponse(),
      'GET /api/v1/drivers': () => [builtIn],
      'GET /api/v1/drivers/windows': () => ({ devices: [device], packages: [driver], message: null }),
      'POST /api/v1/drivers/windows/ch341ser-1a2b3c4d/install': (_url, init) => {
        installs.push(JSON.parse(String(init!.body)));
        return { devices: [], packages: [{ ...driver, matches: [], installed: 'oem42.inf' }], message: 'Installed: the device uses it now.' };
      },
    });
    renderApp('/drivers');

    const devices = await screen.findByRole('list', { name: 'Devices' });
    expect(within(devices).getByRole('listitem', { name: 'USB Serial' })).toHaveTextContent('Windows has no driver for it');
    const item = within(screen.getByRole('list', { name: 'Windows drivers' })).getByRole('listitem', { name: 'CH341SER.INF' });
    expect(item).toHaveTextContent('Signed by Microsoft Windows Hardware Compatibility Publisher');

    await userEvent.click(within(item).getByRole('button', { name: 'Install for USB Serial' }));
    expect(await screen.findByRole('dialog', { name: 'Install CH341SER.INF for USB Serial?' })).toHaveTextContent('You confirm it again on the PC');
    await confirmIt();
    await waitFor(() => expect(installs).toEqual([{ device: device.instanceId, restorePoint: true }]));
    expect(await screen.findByText('Installed as oem42.inf')).toBeInTheDocument();
    expect(await screen.findByText('Every connected device has a driver.')).toBeInTheDocument();
  });
});
