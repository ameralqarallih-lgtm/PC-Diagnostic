export interface GamingSearch {
  /** The session shown. */
  session?: number;
}

/** Validates ?session=. */
export function parseGamingSearch(search: Record<string, unknown>): GamingSearch {
  const id = Number(search.session);
  return Number.isInteger(id) && id > 0 ? { session: id } : {};
}
