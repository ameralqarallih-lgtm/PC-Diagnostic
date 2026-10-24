import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, createRootRoute, createRouter, RouterProvider } from '@tanstack/react-router';
import type { ReactNode } from 'react';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { expect, vi } from 'vitest';
import { ConfirmProvider } from '@/components/ui/confirm';
import { ToastProvider } from '@/components/ui/toast';
import { createAppRouter } from '@/router';

export type Handler = (url: string, init?: RequestInit) => unknown;

function respond(body: unknown, status = 200) {
  if (body === undefined) return Promise.resolve(new Response(null, { status: 204 }));
  return Promise.resolve(new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }));
}

/** A problem response for a handler to return. */
export class Problem {
  constructor(
    readonly status: number,
    readonly body: unknown,
  ) {}
}

/**
 * Replaces fetch with handlers keyed "METHOD /api/v1/path" (query string ignored). A handler returning undefined
 * answers 204; a Problem answers with its status. Unknown requests get 404. Unsaved dashboards answer 204.
 */
export function mockApi(handlers: Record<string, Handler>) {
  const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : input.toString();
    const key = `${init?.method ?? 'GET'} ${url.split('?')[0]}`;
    const handler = handlers[key] ?? (key === 'GET /api/v1/dashboard' ? () => undefined : undefined);
    if (!handler) return respond({ title: 'not mocked: ' + key }, 404);
    const result = handler(url, init);
    return result instanceof Problem ? respond(result.body, result.status) : respond(result);
  });
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

/** Renders the whole app at a path; returns the router so tests can read the URL. */
export function renderApp(path: string) {
  const router = createAppRouter(createMemoryHistory({ initialEntries: [path] }));
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <ToastProvider>
        <ConfirmProvider>
          <RouterProvider router={router} />
        </ConfirmProvider>
      </ToastProvider>
    </QueryClientProvider>,
  );
  return router;
}

/** The JSON body sent with a mocked request. */
export function bodyOf(fetchMock: ReturnType<typeof mockApi>, method: string, path: string): unknown {
  const call = fetchMock.mock.calls.find(([url, init]) => String(url).split('?')[0] === path && (init?.method ?? 'GET') === method);
  return call?.[1]?.body ? JSON.parse(String(call[1].body)) : undefined;
}

/** The query string of a mocked GET. */
export function queryOf(fetchMock: ReturnType<typeof mockApi>, path: string): URLSearchParams | undefined {
  const call = [...fetchMock.mock.calls].reverse().find(([url, init]) => String(url).split('?')[0] === path && (init?.method ?? 'GET') === 'GET');
  return call ? new URLSearchParams(String(call[0]).split('?')[1] ?? '') : undefined;
}

/** Renders a component inside a minimal router, for components with links. */
export function renderInRouter(ui: ReactNode) {
  const router = createRouter({ routeTree: createRootRoute({ component: () => ui }), history: createMemoryHistory({ initialEntries: ['/'] }) });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <ConfirmProvider>
        <RouterProvider router={router} />
      </ConfirmProvider>
    </QueryClientProvider>,
  );
}

/** Confirms the open confirmation (Delete, Remove, Deactivate…): every delete asks first. */
export async function confirmIt() {
  // The confirmation is the last dialog opened (over a picker, for one).
  const dialogs = await screen.findAllByRole('dialog');
  const dialog = dialogs[dialogs.length - 1]!;
  const buttons = within(dialog).getAllByRole('button');
  await userEvent.click(buttons[buttons.length - 1]!);
  await waitFor(() => expect(dialog).not.toBeInTheDocument());
}
