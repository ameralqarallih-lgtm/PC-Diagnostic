import { fireEvent, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it } from 'vitest';
import { DataTable, type Column } from './DataTable';
import { cleanView, compareValues, groupRows, moveItem, sortRows } from './view';

interface Proc {
  name: string;
  pid: number;
  cpu: number | null;
  user: string;
}

const rows: Proc[] = [
  { name: 'chrome.exe', pid: 10, cpu: 12, user: 'me' },
  { name: 'svchost.exe', pid: 4, cpu: 0.5, user: 'SYSTEM' },
  { name: 'game.exe', pid: 200, cpu: 60, user: 'me' },
  { name: 'idle.exe', pid: 7, cpu: null, user: 'SYSTEM' },
  { name: 'chrome.exe', pid: 11, cpu: 3, user: 'me' },
];

const columns: Column<Proc>[] = [
  { id: 'name', header: 'Process', value: (p) => p.name },
  { id: 'pid', header: 'PID', value: (p) => p.pid, align: 'right', firstDesc: false },
  { id: 'cpu', header: 'CPU', value: (p) => p.cpu, cell: (p) => (p.cpu == null ? '—' : `${p.cpu} %`), align: 'right', firstDesc: true },
  { id: 'user', header: 'User', value: (p) => p.user },
];

afterEach(() => window.localStorage.clear());

const names = () => screen.getAllByRole('row').slice(1).map((r) => within(r).queryAllByRole('cell')[0]?.textContent);

describe('sorting and grouping', () => {
  it('compares numbers, natural text and missing values last', () => {
    expect([10, 2, null, 33].sort((a, b) => compareValues(a, b))).toEqual([2, 10, 33, null]);
    expect([10, 2, null, 33].sort((a, b) => compareValues(a, b, true))).toEqual([33, 10, 2, null]);
    expect(['item 10', 'Item 2', 'item 1'].sort((a, b) => compareValues(a, b))).toEqual(['item 1', 'Item 2', 'item 10']);
  });

  it('sorts by several keys and keeps equal rows in order', () => {
    const sorted = sortRows(rows, columns, [{ id: 'user', desc: false }, { id: 'cpu', desc: true }]);
    expect(sorted.map((p) => p.pid)).toEqual([200, 10, 11, 4, 7]);
  });

  it('nests groups, orders them by their column and counts every row below', () => {
    const groups = groupRows(rows, columns, { groupBy: ['user', 'name'], sort: [{ id: 'cpu', desc: true }] })!;
    expect(groups.map((g) => [g.label, g.count])).toEqual([
      ['me', 3],
      ['SYSTEM', 2],
    ]);
    expect(groups[0]!.groups!.map((g) => [g.label, g.count])).toEqual([
      ['chrome.exe', 2],
      ['game.exe', 1],
    ]);
    expect(groups[0]!.groups![0]!.rows!.map((p) => p.pid)).toEqual([10, 11]);
  });

  it('drops unknown or repeated columns from a remembered view', () => {
    const fallback = { groupBy: [], sort: [{ id: 'cpu', desc: true }] };
    expect(cleanView({ groupBy: ['user', 'gone', 'user'], sort: [{ id: 'gone', desc: true }, { id: 'pid', desc: false }, { id: 'pid', desc: true }] }, columns, fallback)).toEqual({
      groupBy: ['user'],
      sort: [{ id: 'pid', desc: false }],
    });
    expect(cleanView(null, columns, fallback)).toBe(fallback);
    expect(moveItem(['a', 'b', 'c'], 0, 3)).toEqual(['b', 'c', 'a']);
    expect(moveItem(['a', 'b', 'c'], 2, 0)).toEqual(['c', 'a', 'b']);
  });
});

describe('DataTable', () => {
  const table = (viewKey = 'test') => (
    <DataTable viewKey={viewKey} label="Processes" rows={rows} columns={columns} rowKey={(p) => p.pid} defaultView={{ groupBy: [], sort: [{ id: 'cpu', desc: true }] }} />
  );

  it('opens with its default sort; a header click sorts, Shift+click adds a key', async () => {
    render(table());
    expect(names()).toEqual(['game.exe', 'chrome.exe', 'chrome.exe', 'svchost.exe', 'idle.exe']);
    expect(screen.getByRole('columnheader', { name: /CPU/ })).toHaveAttribute('aria-sort', 'descending');

    await userEvent.click(screen.getByRole('button', { name: /^Process/ }));
    expect(names()).toEqual(['chrome.exe', 'chrome.exe', 'game.exe', 'idle.exe', 'svchost.exe']);

    fireEvent.click(screen.getByRole('button', { name: /^PID/ }), { shiftKey: true });
    const sort = screen.getByRole('group', { name: 'Sort by' });
    expect(within(sort).getByRole('button', { name: 'Process, ascending' })).toBeInTheDocument();
    expect(within(sort).getByRole('button', { name: 'PID, ascending' })).toBeInTheDocument();
  });

  it('groups by a column dragged onto "Group by", folds groups, and remembers the view', async () => {
    const { unmount } = render(table());
    const header = screen.getByRole('columnheader', { name: /User/ });
    const zone = screen.getByRole('group', { name: 'Group by' });
    fireEvent.dragStart(header, { dataTransfer: { setData: () => undefined, effectAllowed: 'move' } });
    fireEvent.dragOver(zone, { dataTransfer: { dropEffect: 'move' } });
    fireEvent.drop(zone);

    const me = screen.getByRole('button', { name: /User:\s*me\s*3/ });
    expect(me).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByRole('button', { name: /User:\s*SYSTEM\s*2/ })).toBeInTheDocument();
    await userEvent.click(me);
    expect(screen.queryByText('game.exe')).not.toBeInTheDocument();
    expect(screen.getByText('svchost.exe')).toBeInTheDocument();

    unmount();
    render(table());
    expect(screen.getByRole('button', { name: /User:\s*me\s*3/ })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: /Reset/ }));
    expect(screen.queryByRole('button', { name: /User:\s*me/ })).not.toBeInTheDocument();
  });

  it('works without a mouse: add from the menus, move with arrow keys, reverse and remove', async () => {
    render(table());
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Add a grouping' }), 'user');
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Add a grouping' }), 'name');
    const group = screen.getByRole('group', { name: 'Group by' });
    expect(within(group).getAllByRole('button', { name: /^(User|Process)$/ }).map((b) => b.textContent)).toEqual(['User', 'Process']);

    within(group).getByRole('button', { name: 'Process' }).focus();
    await userEvent.keyboard('{ArrowLeft}');
    expect(within(group).getAllByRole('button', { name: /^(User|Process)$/ }).map((b) => b.textContent)).toEqual(['Process', 'User']);

    const sort = screen.getByRole('group', { name: 'Sort by' });
    await userEvent.click(within(sort).getByRole('button', { name: 'CPU, descending' }));
    expect(within(sort).getByRole('button', { name: 'CPU, ascending' })).toBeInTheDocument();
    await userEvent.click(within(group).getByRole('button', { name: 'Remove User' }));
    expect(within(group).queryByRole('button', { name: 'User' })).not.toBeInTheDocument();
  });

  it('in list mode renders rows freely and offers its fields to drag', () => {
    render(
      <DataTable
        viewKey="list"
        label="Events"
        rows={rows}
        columns={columns}
        rowKey={(p) => p.pid}
        defaultView={{ groupBy: ['user'], sort: [{ id: 'name', desc: false }] }}
        renderRow={(p) => <span>{`${p.name} #${p.pid}`}</span>}
        compact
      />,
    );

    const list = screen.getByRole('list', { name: 'Events' });
    expect(within(list).getByText('chrome.exe #10')).toBeInTheDocument();
    expect(screen.queryByTestId('table-view-bars')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /Grouped by User, sorted by Process ↑/ }));
    expect(screen.getByLabelText('Fields to group or sort by')).toHaveTextContent('PID');
  });
});

describe('figures as text', () => {
  it('sorts by the value, not the letters', async () => {
    const { measureValue } = await import('./view');
    expect(['980 MB', '38.9 GB', '—', '1.2 GB'].map(measureValue)).toEqual([980 * 1024 ** 2, 38.9 * 1024 ** 3, null, 1.2 * 1024 ** 3]);
    expect(measureValue('2 h 5 min')).toBe(7500);
    expect(measureValue('61 min')).toBe(3660);
    expect(measureValue('141 MB/s')).toBe(141 * 1024 ** 2);
    expect(measureValue('8.8 %')).toBe(8.8);
    expect(measureValue('1,234')).toBe(1234);
    expect(measureValue('vmmemWSL')).toBe('vmmemWSL');
    expect(measureValue('22:17:41')).toBe('22:17:41');
  });
});
