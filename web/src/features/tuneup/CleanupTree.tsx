import { useQuery } from '@tanstack/react-query';
import { ChevronRight, File, Folder } from 'lucide-react';
import { useState } from 'react';
import { apiGet } from '@/lib/api/client';
import type { CleanupEntry, CleanupListing } from '@/lib/api/types';
import { formatBytes } from '@/lib/format';
import { cn } from '@/lib/utils';

function useListing(target: string, root: number, path: string) {
  return useQuery({
    queryKey: ['tuneup', 'cleanup-files', target, root, path],
    queryFn: ({ signal }) => apiGet<CleanupListing>(`/tuneup/cleanup/${encodeURIComponent(target)}/files?root=${root}&path=${encodeURIComponent(path)}`, signal),
    staleTime: 30_000,
  });
}

const dateFormat = new Intl.DateTimeFormat(undefined, {
  dateStyle: 'medium',
  timeStyle: 'short',
});

/**
 * What a clean-up target holds, as a tree: its folders, then their folders and files, opened one level at a time.
 * Each line shows its size, how much of it the clean-up would remove (old enough, of the target's kind), its files
 * and when it last changed.
 */
export function CleanupTree({ target }: { target: string }) {
  return (
    <div role="tree" aria-label="Files" className="rounded-md border border-border bg-surface-muted/40 text-sm">
      <div className="grid grid-cols-[1fr_6rem_7rem_5rem_10rem] gap-2 border-b border-border px-2 py-1 text-xs font-medium text-muted max-md:hidden">
        <span>Name</span>
        <span className="text-right">Size</span>
        <span className="text-right">Would be removed</span>
        <span className="text-right">Files</span>
        <span>Changed</span>
      </div>
      <Level target={target} root={-1} path="" depth={0} />
    </div>
  );
}

function Level({ target, root, path, depth }: { target: string; root: number; path: string; depth: number }) {
  const listing = useListing(target, root, path);
  const indent = { paddingLeft: `${0.5 + depth * 1.1}rem` };
  if (listing.isLoading)
    return (
      <p className="py-1 text-xs text-muted" style={indent}>
        Reading…
      </p>
    );
  if (listing.error)
    return (
      <p className="py-1 text-xs text-danger" style={indent}>
        It could not be read: {listing.error.message}
      </p>
    );
  const data = listing.data;
  if (!data || (data.entries.length === 0 && data.note))
    return (
      <p className="py-1 text-xs text-muted" style={indent}>
        {data?.note ?? 'Empty.'}
      </p>
    );
  if (data.entries.length === 0)
    return (
      <p className="py-1 text-xs text-muted" style={indent}>
        Empty.
      </p>
    );
  return (
    <ul role="group" className="flex flex-col">
      {data.entries.map((e) => (
        <Node key={`${e.root}|${e.path}|${e.name}`} target={target} entry={e} depth={depth} />
      ))}
      {data.more > 0 && (
        <li className="py-1 text-xs text-muted" style={indent}>
          and {data.more.toLocaleString()} smaller ones
        </li>
      )}
    </ul>
  );
}

function Node({ target, entry, depth }: { target: string; entry: CleanupEntry; depth: number }) {
  const [open, setOpen] = useState(false);
  const label = entry.name;
  const content = (
    <>
      <span className="flex min-w-0 items-center gap-1.5" style={{ paddingLeft: `${depth * 1.1}rem` }}>
        {entry.folder ? <ChevronRight className={cn('size-3.5 shrink-0 text-muted transition-transform', open && 'rotate-90')} aria-hidden /> : <span className="w-3.5 shrink-0" />}
        {entry.folder ? <Folder className="size-4 shrink-0 text-warning" aria-hidden /> : <File className="size-4 shrink-0 text-muted" aria-hidden />}
        <span className="truncate" title={label}>
          {label}
        </span>
      </span>
      <span className="text-right tabular-nums">
        {formatBytes(entry.bytes)}
        {entry.partial ? '+' : ''}
      </span>
      <span className={cn('text-right tabular-nums', entry.removableBytes > 0 ? 'text-foreground' : 'text-muted')}>{entry.removableBytes > 0 ? formatBytes(entry.removableBytes) : '—'}</span>
      <span className="text-right tabular-nums text-muted">{entry.folder ? entry.files.toLocaleString() : ''}</span>
      <span className="truncate text-xs text-muted">{entry.modified ? dateFormat.format(new Date(entry.modified)) : ''}</span>
    </>
  );
  const row = 'grid w-full grid-cols-[1fr_6rem_7rem_5rem_10rem] items-center gap-2 px-2 py-1 text-left max-md:grid-cols-[1fr_5rem]';
  return (
    <li role="treeitem" aria-expanded={entry.folder ? open : undefined} aria-label={label}>
      {entry.folder ? (
        <button type="button" onClick={() => setOpen((o) => !o)} className={cn(row, 'hover:bg-accent/10')}>
          {content}
        </button>
      ) : (
        <div className={row}>{content}</div>
      )}
      {entry.folder && open && <Level target={target} root={entry.root} path={entry.path} depth={depth + 1} />}
    </li>
  );
}
