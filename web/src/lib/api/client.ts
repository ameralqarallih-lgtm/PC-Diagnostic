/** Minimal typed fetch wrapper for the PcDiag.Web API. Same-origin only; the session cookie is sent automatically. */

export class ApiError extends Error {
  readonly status: number;
  readonly title: string | undefined;

  constructor(status: number, title?: string, detail?: string) {
    super(detail ?? title ?? `Request failed with status ${status}`);
    this.name = 'ApiError';
    this.status = status;
    this.title = title;
  }

  /** The session is missing or expired: the user must reopen the dashboard from the tray. */
  get isUnauthorized(): boolean {
    return this.status === 401;
  }
}

/** fetch, with network failures (web host stopped or restarted) turned into a clear error. */
async function send(url: string, init: RequestInit): Promise<Response> {
  try {
    return await fetch(url, init);
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') throw error;
    throw new ApiError(
      0,
      'Connection lost',
      'The dashboard cannot reach PC Diagnostic. If it was restarted, open the dashboard again from the tray icon.',
    );
  }
}

async function toApiError(response: Response): Promise<ApiError> {
  try {
    const problem = (await response.json()) as { title?: string; detail?: string; errors?: Record<string, string[]> };
    const details = problem.errors ? Object.values(problem.errors).flat().join(' ') : undefined;
    return new ApiError(response.status, problem.title, problem.detail ?? details);
  } catch {
    return new ApiError(response.status);
  }
}

export async function apiGet<T>(path: string, signal?: AbortSignal): Promise<T> {
  const response = await send(`/api/v1${path}`, {
    credentials: 'same-origin',
    headers: { Accept: 'application/json' },
    signal,
  });
  if (!response.ok) throw await toApiError(response);
  return (await response.json()) as T;
}

/** GET that returns null for 204 No Content. */
export async function apiGetOptional<T>(path: string, signal?: AbortSignal): Promise<T | null> {
  const response = await send(`/api/v1${path}`, {
    credentials: 'same-origin',
    headers: { Accept: 'application/json' },
    signal,
  });
  if (!response.ok) throw await toApiError(response);
  return response.status === 204 ? null : ((await response.json()) as T);
}

/** PUT, PATCH or DELETE; returns the JSON body when there is one. */
export async function apiRequest<T = void>(method: 'PUT' | 'PATCH' | 'DELETE', path: string, body?: unknown): Promise<T | undefined> {
  const response = await send(`/api/v1${path}`, {
    method,
    credentials: 'same-origin',
    headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
    body: body === undefined ? null : JSON.stringify(body),
  });
  if (!response.ok) throw await toApiError(response);
  return response.status === 204 ? undefined : ((await response.json()) as T);
}

export async function apiSend(method: 'PUT' | 'DELETE', path: string, body?: unknown): Promise<void> {
  const response = await send(`/api/v1${path}`, {
    method,
    credentials: 'same-origin',
    headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
    body: body === undefined ? null : JSON.stringify(body),
  });
  if (!response.ok) throw await toApiError(response);
}

export async function apiPost<T = void>(path: string, body?: unknown): Promise<T> {
  const response = await send(`/api/v1${path}`, {
    method: 'POST',
    credentials: 'same-origin',
    headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
    body: body === undefined ? null : JSON.stringify(body),
  });
  if (!response.ok) throw await toApiError(response);
  return response.status === 204 ? (undefined as T) : ((await response.json()) as T);
}

/** POSTs a file as the request body (its own content type); the JSON answer. */
export async function apiUpload<T>(path: string, file: Blob): Promise<T> {
  const response = await send(`/api/v1${path}`, {
    method: 'POST',
    credentials: 'same-origin',
    headers: { Accept: 'application/json', 'Content-Type': file.type || 'application/octet-stream' },
    body: file,
  });
  if (!response.ok) throw await toApiError(response);
  return (await response.json()) as T;
}
