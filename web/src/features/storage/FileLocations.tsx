import { ArrowDown, ArrowUp, Cloud, Database, FolderOpen, HardDrive, History, KeyRound, Play, Plus, RefreshCw, Square, Star, Trash2, Undo2 } from 'lucide-react';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useId, useRef, useState } from 'react';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { ConfirmAction, type PendingAction } from '@/features/tuneup/ConfirmAction';
import type { FileKind, StorageJob, StorageLocation, StorageLocationRequest, StorageLocationView } from '@/lib/api/types';
import { formatBytes } from '@/lib/format';
import { useFillHeight } from '@/lib/useFillHeight';
import { cn } from '@/lib/utils';
import { useChangeLocation, useFileLocations, useLayoutBackups, useLocalStore, useLocalStoreAction, useRestoreBackup, useSyncLocations, useTestLocation } from './useStorage';

const INPUT = 'h-9 w-full rounded-md border border-border bg-background px-2 text-sm';

/** What each kind of file is. */
export const FILE_KINDS: Record<FileKind, { name: string; hint: string }> = {
  pictures: { name: 'Panel pictures', hint: 'The pictures behind on-screen panels' },
  characters: { name: 'Characters', hint: 'The characters that come with PC Diagnostic and those imported or generated' },
  drafts: { name: 'Character drafts', hint: "The character generator's drawings to choose from" },
  settings: { name: 'Settings and layouts', hint: "Copies of the settings, the on-screen layout, the dashboards, the rules and the expected hardware (never the paired devices' keys)" },
  backups: { name: 'Layout backups', hint: 'The on-screen layout as it was before it was changed' },
  drivers: { name: 'Device drivers', hint: 'Driver packages imported for devices' },
  exports: { name: 'Kept exports', hint: 'Reports exported with "Keep a copy"' },
  diagnostics: { name: 'Diagnostic reports', hint: "The period diagnostics' reports" },
  reports: { name: 'Problem reports', hint: 'Problem reports, sent or waiting to be' },
};

const kindName = (kind: string) => FILE_KINDS[kind as FileKind]?.name ?? kind;

/** "D:\Backups\PcDiagnostic", "minio.lan:9000 · pcdiag/home", "The data folder on this PC". */
function whereOf(l: StorageLocation): string {
  if (l.kind === 'pc') return 'The data folder on this PC';
  if (l.local) return `SeaweedFS on this PC (127.0.0.1:${l.local.s3Port}) · its files in ${l.local.folder}`;
  if (l.kind === 'folder') return `${(l.path ?? '').replace(/[\\/]+$/, '')}\\${l.prefix || 'PcDiagnostic'}`;
  const host = l.endpoint ? l.endpoint.replace(/^https?:\/\//, '').replace(/\/+$/, '') : 'Amazon S3';
  return `${host} · ${l.bucket ?? ''}${l.prefix ? `/${l.prefix.replace(/^\/+|\/+$/g, '')}` : ''}${l.region ? ` · ${l.region}` : ''}`;
}

const LOCATION_ICON = { pc: HardDrive, folder: FolderOpen, s3: Cloud } as const;

/**
 * Where files are kept (Storage page): This PC, folders (another drive, a network share) and S3-compatible services
 * (AWS, MinIO, Cloudflare R2, Wasabi, Backblaze B2…), the kinds each keeps, their order (files are read from the first
 * that has them), a test before one is added, its keys (set, never shown again), and the job copying what is missing.
 */
export function WhereFilesAreKept() {
  const locations = useFileLocations();
  const change = useChangeLocation();
  const sync = useSyncLocations();
  const [adding, setAdding] = useState<'folder' | 's3' | null>(null);
  const [keysFor, setKeysFor] = useState<string | null>(null);
  const [pending, setPending] = useState<PendingAction | null>(null);
  const data = locations.data;

  const move = (index: number, by: -1 | 1) => {
    if (!data) return;
    const others = data.locations.slice(1).map((v) => v.location.id);
    const at = index - 1;
    const to = at + by;
    if (at < 0 || to < 0 || to >= others.length) return;
    [others[at], others[to]] = [others[to]!, others[at]!];
    change.mutate({ action: 'order', ids: others });
  };

  const askRemove = (location: StorageLocation) =>
    setPending({
      title: `Remove ${location.name}?`,
      confirmLabel: 'Remove',
      danger: true,
      body: (
        <p>
          What only {location.name} has is copied to the other locations first. Nothing is deleted there: its files stay where they are, and PC Diagnostic stops using it.
          {location.default && ' It is where files are kept by default: This PC is again.'}
          {location.local && ' SeaweedFS is stopped; its folder stays (and it is not set up again by itself).'}
        </p>
      ),
      // Its files could not all be copied (it cannot be reached): asked whether to remove it anyway.
      run: () =>
        change.mutate(
          { action: 'remove', id: location.id },
          {
            onError: (error) =>
              setPending({
                title: `Remove ${location.name} anyway?`,
                confirmLabel: 'Remove anyway',
                danger: true,
                body: (
                  <p>
                    Its files could not all be copied to the other locations ({error instanceof Error ? error.message : 'it cannot be reached'}). Removed anyway, what only it has stays there, not used
                    by PC Diagnostic.
                  </p>
                ),
                run: () => change.mutate({ action: 'remove', id: location.id, force: true }),
              }),
          },
        ),
    });

  const askDefault = (location: StorageLocation) => {
    const current = data?.locations.find((v) => v.location.default)?.location;
    setPending({
      title: `Keep files in ${location.name} by default?`,
      confirmLabel: 'Make it the default',
      body: (
        <p>
          {location.name} keeps every kind of file from now on, and what is kept so far is copied there.{' '}
          {current && current.id !== location.id && (current.kind === 'pc' ? 'This PC keeps only what the recorder reads (panel pictures, characters and device drivers).' : `${current.name} stops keeping them (its files stay there).`)}
        </p>
      ),
      run: () => change.mutate({ action: 'default', id: location.id, move: true }),
    });
  };

  return (
    <Card>
      <CardHeader className="flex-wrap">
        <CardTitle className="flex items-center gap-2">
          <FolderOpen className="size-4" aria-hidden /> Where files are kept
        </CardTitle>
        <div className="flex flex-wrap gap-2">
          <Button size="sm" variant="outline" disabled={!data || adding !== null} onClick={() => setAdding('folder')}>
            <Plus aria-hidden /> Add a folder
          </Button>
          <Button size="sm" variant="outline" disabled={!data || adding !== null} onClick={() => setAdding('s3')}>
            <Cloud aria-hidden /> Add an S3 service
          </Button>
          <Button size="sm" variant="ghost" disabled={!data || data.job.running || sync.isPending} onClick={() => sync.mutate()} title="Copies what is missing to every location now">
            <RefreshCw aria-hidden className={cn(data?.job.running && 'animate-spin')} /> Bring up to date
          </Button>
        </div>
      </CardHeader>
      <CardContent className="flex flex-col gap-3">
        <p className="text-xs text-muted">
          Files are kept in the default location, and in any other location ticked for them below (as copies); they are read from the first one that has them. What the recorder reads (panel
          pictures, characters and device drivers) always stays on This PC too. Locations are changed on this PC only.
        </p>
        <LocalStorage />
        {locations.error && <p className="text-sm text-danger">The storage locations could not be read.</p>}
        {!data && !locations.error && <p className="text-sm text-muted">Reading…</p>}
        {data && <JobLine job={data.job} />}
        {data && <WhatGoesWhere views={data.locations} kinds={data.kinds} onThisPc={data.onThisPc} busy={change.isPending} onKeeps={(l, keeps) => change.mutate({ action: 'change', id: l.id, keeps })} />}
        {data && (
          <ul aria-label="Storage locations" className="divide-y divide-border rounded-md border border-border">
            {data.locations.map((view, index) => (
              <LocationRow
                key={view.location.id}
                view={view}
                index={index}
                count={data.locations.length}
                kinds={data.kinds}
                onThisPc={data.onThisPc}
                busy={change.isPending}
                onDefault={() => askDefault(view.location)}
                onMove={(by) => move(index, by)}
                onRemove={() => askRemove(view.location)}
                keysOpen={keysFor === view.location.id}
                onKeys={() => setKeysFor((k) => (k === view.location.id ? null : view.location.id))}
                onSaveKeys={(accessKey, secretKey) => change.mutate({ action: 'change', id: view.location.id, accessKey, secretKey }, { onSuccess: () => setKeysFor(null) })}
              />
            ))}
          </ul>
        )}
        {adding && data && <AddLocation key={adding} kind={adding} kinds={data.kinds} onDone={() => setAdding(null)} />}
      </CardContent>
      <ConfirmAction pending={pending} onClose={() => setPending(null)} />
    </Card>
  );
}

/** "Copying files: 12 of 40", or how the last run went. */
function JobLine({ job }: { job: StorageJob }) {
  if (job.running) {
    return (
      <div role="status" className="flex flex-col gap-1 text-xs">
        <span>
          Copying files to the locations: {job.done} of {job.total}
        </span>
        <div className="h-1.5 overflow-hidden rounded-full bg-surface-muted" aria-hidden>
          <div className="h-full rounded-full bg-accent" style={{ width: `${job.total > 0 ? (job.done / job.total) * 100 : 0}%` }} />
        </div>
      </div>
    );
  }
  if (job.problem) return <p className="rounded-md border border-warning/40 bg-warning/10 px-3 py-2 text-xs">Not everything was copied: {job.problem}</p>;
  if (job.finished && job.copied > 0) {
    return (
      <p className="text-xs text-muted">
        {job.copied === 1 ? '1 file' : `${job.copied} files`} copied, {new Date(job.finished).toLocaleString(undefined, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })}.
      </p>
    );
  }
  return null;
}

function LocationRow({
  view,
  index,
  count,
  kinds,
  busy,
  onDefault,
  onMove,
  onRemove,
  keysOpen,
  onKeys,
  onSaveKeys,
}: {
  view: StorageLocationView;
  index: number;
  count: number;
  kinds: FileKind[];
  onThisPc: FileKind[];
  busy: boolean;
  onDefault: () => void;
  onMove: (by: -1 | 1) => void;
  onRemove: () => void;
  keysOpen: boolean;
  onKeys: () => void;
  onSaveKeys: (accessKey: string, secretKey: string) => void;
}) {
  const l = view.location;
  const Icon = l.local ? Database : (LOCATION_ICON[l.kind] ?? FolderOpen);
  const keeps = kinds.filter((k) => l.keeps.includes(k)).map(kindName);
  return (
    <li className="flex flex-col gap-2 p-3" aria-label={l.name}>
      <div className="flex flex-wrap items-start gap-3">
        <Icon className="mt-0.5 size-4 shrink-0 text-muted" aria-hidden />
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-sm font-medium">{l.name}</span>
            {l.local?.stopped ? <Badge>Stopped</Badge> : view.problem ? <Badge tone="danger">Not reached</Badge> : <Badge tone="success">Working</Badge>}
            {l.default && <Badge tone="accent">Default</Badge>}
            {index === 0 && <Badge>Read first</Badge>}
          </div>
          <span className="block break-all text-xs text-muted">
            {whereOf(l)}
            {l.kind === 's3' && (view.hasKeys ? ` · key …${view.accessKeyEnd ?? ''}` : ' · no keys')}
          </span>
          <span className="block text-xs text-muted">{keeps.length > 0 ? `Keeps ${keeps.join(', ')}` : 'Keeps nothing now: tick what it keeps above, or remove it.'}</span>
          {view.problem && !l.local?.stopped && <span className="block text-xs text-danger">{view.problem}</span>}
        </div>
        {!l.default && (
          <Button size="sm" variant="ghost" aria-label={`Make ${l.name} the default`} title="Keep files here by default" disabled={busy} onClick={onDefault}>
            <Star aria-hidden /> Make default
          </Button>
        )}
        {l.kind !== 'pc' && (
          <div className="flex items-center gap-0.5">
            {l.kind === 's3' && !l.local && (
              <Button size="icon" variant="ghost" aria-label={`Change the keys of ${l.name}`} aria-expanded={keysOpen} title="Change the keys" onClick={onKeys}>
                <KeyRound aria-hidden />
              </Button>
            )}
            <Button size="icon" variant="ghost" aria-label={`Move ${l.name} up`} title="Read before the one above" disabled={busy || index <= 1} onClick={() => onMove(-1)}>
              <ArrowUp aria-hidden />
            </Button>
            <Button size="icon" variant="ghost" aria-label={`Move ${l.name} down`} title="Read after the one below" disabled={busy || index >= count - 1} onClick={() => onMove(1)}>
              <ArrowDown aria-hidden />
            </Button>
            <Button size="icon" variant="ghost" aria-label={`Remove ${l.name}`} title="Stop using it (its files stay there)" disabled={busy} className="hover:text-danger" onClick={onRemove}>
              <Trash2 aria-hidden />
            </Button>
          </div>
        )}
      </div>
      {keysOpen && <KeysForm name={l.name} busy={busy} onSave={onSaveKeys} onCancel={onKeys} />}
    </li>
  );
}

/**
 * PC Diagnostic Storage: SeaweedFS run on this PC (as Video Clipper's built-in storage), where files are kept by
 * default; set up here (its program downloaded once and checked when it was not installed with PC Diagnostic),
 * started and stopped.
 */
function LocalStorage() {
  const id = useId();
  const local = useLocalStore();
  const act = useLocalStoreAction();
  const [form, setForm] = useState<{ folder: string; makeDefault: boolean } | null>(null);
  const v = local.data;
  // Set up (or failed): the locations as they are now (it may be the default, its files being copied).
  const client = useQueryClient();
  const settingUp = v?.setup.running ?? false;
  const wasSettingUp = useRef(false);
  useEffect(() => {
    if (wasSettingUp.current && !settingUp) void client.invalidateQueries({ queryKey: ['storage-locations'] });
    wasSettingUp.current = settingUp;
  }, [settingUp, client]);
  if (!v) return null;
  const setup = v.setup;
  const set = v.locationId != null;
  const program = v.program === 'shipped' ? 'installed with PC Diagnostic' : v.program === 'downloaded' ? 'downloaded' : 'downloaded once when set up';
  return (
    <section aria-label="PC Diagnostic Storage" className="flex flex-col gap-2 rounded-md border border-accent/30 bg-accent/5 p-3">
      <div className="flex flex-wrap items-center gap-2">
        <Database className="size-4 text-accent" aria-hidden />
        <span className="text-sm font-medium">{v.name}</span>
        <span className="text-xs text-muted">
          SeaweedFS {v.version} · {program}
        </span>
        {set && (v.stopped ? <Badge>Stopped</Badge> : v.running ? <Badge tone="success">Running</Badge> : <Badge tone="warning">Starting…</Badge>)}
        {set && v.isDefault && <Badge tone="accent">Default</Badge>}
        <span className="ml-auto flex gap-2">
          {!set && !setup.running && !form && (
            <Button size="sm" onClick={() => setForm({ folder: v.defaultFolder, makeDefault: true })}>
              <Database aria-hidden /> Set it up
            </Button>
          )}
          {set &&
            (v.stopped ? (
              <Button size="sm" variant="outline" disabled={act.isPending} onClick={() => act.mutate({ action: 'start' })}>
                <Play aria-hidden /> Start
              </Button>
            ) : (
              <Button size="sm" variant="outline" disabled={act.isPending} onClick={() => act.mutate({ action: 'stop' })}>
                <Square aria-hidden /> Stop
              </Button>
            ))}
        </span>
      </div>
      <p className="text-xs text-muted">
        {set
          ? v.stopped
            ? `Stopped · its files are in ${v.folder}`
            : v.running
              ? `Running on this PC only (127.0.0.1:${v.port}) · its files are in ${v.folder}`
              : `Starting… · its files are in ${v.folder}`
          : 'An S3-compatible object store that runs on this PC only (SeaweedFS, Apache-2.0), started with the recorder and kept running. Files are kept in it by default; another S3 service or a folder can be the default instead.'}
      </p>
      {set && !v.stopped && v.problem && <p className="text-xs text-danger">{v.problem}</p>}
      {setup.running && (
        <div role="status" className="flex flex-col gap-1 text-xs">
          <span>{setup.step ?? 'Setting it up'}…</span>
          <div className="h-1.5 overflow-hidden rounded-full bg-surface-muted" aria-hidden>
            <div className="h-full rounded-full bg-accent transition-[width]" style={{ width: `${Math.round(setup.done * 100)}%` }} />
          </div>
        </div>
      )}
      {!set && !setup.running && setup.problem && <p className="rounded-md border border-danger/40 bg-danger/10 px-3 py-2 text-xs">It was not set up (nothing was kept): {setup.problem}</p>}
      {form && !set && !setup.running && (
        <form
          aria-label="Set up PC Diagnostic Storage"
          className="flex flex-col gap-2 rounded-md border border-border bg-background p-3"
          onSubmit={(e) => {
            e.preventDefault();
            act.mutate({ action: 'setup', folder: form.folder.trim(), makeDefault: form.makeDefault }, { onSuccess: () => setForm(null) });
          }}
        >
          <label className="flex flex-col gap-1 text-xs" htmlFor={`${id}-folder`}>
            Its files go in
            <input id={`${id}-folder`} className={INPUT} spellCheck={false} value={form.folder} onChange={(e) => setForm({ ...form, folder: e.target.value })} />
          </label>
          <label className="flex items-center gap-2 text-xs">
            <input type="checkbox" className="size-4 accent-[var(--accent)]" checked={form.makeDefault} onChange={(e) => setForm({ ...form, makeDefault: e.target.checked })} />
            Keep files in it by default (This PC keeps only what the recorder reads; what is there now is copied to it)
          </label>
          <ul className="list-disc pl-5 text-xs text-muted">
            {v.program == null && <li>SeaweedFS ({formatBytes(v.downloadBytes)}) is downloaded once from its releases on GitHub and checked against its known SHA-256.</li>}
            <li>It runs on this PC only (127.0.0.1): nothing is reachable from the network, and no firewall rule is added.</li>
            <li>Its keys are made up for it and kept protected on this PC (Windows DPAPI).</li>
          </ul>
          <div className="flex gap-2">
            <Button size="sm" type="submit" disabled={act.isPending || !form.folder.trim()}>
              Set it up
            </Button>
            <Button size="sm" type="button" variant="ghost" onClick={() => setForm(null)}>
              Cancel
            </Button>
          </div>
        </form>
      )}
    </section>
  );
}

/**
 * What goes where: each kind of file against each location, ticked where it is kept (a kind is kept in every location
 * ticked for it; the recorder's kinds always on This PC).
 */
function WhatGoesWhere({ views, kinds, onThisPc, busy, onKeeps }: { views: StorageLocationView[]; kinds: FileKind[]; onThisPc: FileKind[]; busy: boolean; onKeeps: (location: StorageLocation, keeps: FileKind[]) => void }) {
  return (
    <div className="overflow-x-auto rounded-md border border-border">
      <table aria-label="What goes where" className="w-full text-xs">
        <thead>
          <tr className="border-b border-border bg-surface-muted/40">
            <th scope="col" className="px-3 py-2 text-left font-medium">
              Kept in
            </th>
            {views.map(({ location: l }) => (
              <th key={l.id} scope="col" className="px-2 py-2 text-center font-medium">
                <span className="block">{l.name}</span>
                {l.default && <span className="block text-[10px] font-normal text-accent">default</span>}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {kinds.map((kind) => (
            <tr key={kind} className="border-b border-border last:border-0">
              <th scope="row" className="px-3 py-2 text-left font-normal" title={FILE_KINDS[kind]?.hint}>
                {kindName(kind)}
              </th>
              {views.map(({ location: l }) => {
                const on = l.keeps.includes(kind);
                const read = l.kind === 'pc' && onThisPc.includes(kind);
                const fixed = read || l.default === true;
                return (
                  <td key={l.id} className="px-2 py-2 text-center">
                    <input
                      type="checkbox"
                      className="size-4 accent-[var(--accent)]"
                      aria-label={`Keep ${kindName(kind)} in ${l.name}`}
                      title={read ? 'The recorder reads them here: always kept on This PC' : fixed ? 'The default location keeps everything' : undefined}
                      checked={on}
                      disabled={fixed || busy}
                      onChange={() => onKeeps(l, on ? l.keeps.filter((k) => k !== kind) : [...l.keeps, kind])}
                    />
                  </td>
                );
              })}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/** New keys for an S3 service (the secret is never shown again). */
function KeysForm({ name, busy, onSave, onCancel }: { name: string; busy: boolean; onSave: (accessKey: string, secretKey: string) => void; onCancel: () => void }) {
  const id = useId();
  const [access, setAccess] = useState('');
  const [secret, setSecret] = useState('');
  return (
    <form
      aria-label={`Keys of ${name}`}
      className="ml-7 grid gap-2 rounded-md border border-border bg-surface-muted/40 p-3 sm:grid-cols-2"
      onSubmit={(e) => {
        e.preventDefault();
        if (access.trim() && secret.trim()) onSave(access.trim(), secret.trim());
      }}
    >
      <label className="flex flex-col gap-1 text-xs" htmlFor={`${id}-access`}>
        Access key
        <input id={`${id}-access`} className={INPUT} autoComplete="off" spellCheck={false} value={access} onChange={(e) => setAccess(e.target.value)} />
      </label>
      <label className="flex flex-col gap-1 text-xs" htmlFor={`${id}-secret`}>
        Secret key
        <input id={`${id}-secret`} type="password" className={INPUT} autoComplete="new-password" value={secret} onChange={(e) => setSecret(e.target.value)} />
      </label>
      <div className="flex gap-2 sm:col-span-2">
        <Button size="sm" type="submit" disabled={busy || !access.trim() || !secret.trim()}>
          Save the keys
        </Button>
        <Button size="sm" type="button" variant="ghost" onClick={onCancel}>
          Cancel
        </Button>
      </div>
    </form>
  );
}

/** A folder or an S3 service: what it is, what it keeps, a test, then added. */
function AddLocation({ kind, kinds, onDone }: { kind: 'folder' | 's3'; kinds: FileKind[]; onDone: () => void }) {
  const id = useId();
  const test = useTestLocation();
  const change = useChangeLocation();
  const [form, setForm] = useState<StorageLocationRequest>(() =>
    kind === 'folder'
      ? { kind, name: 'Backup drive', keeps: ['backups', 'exports'], path: '', prefix: 'PcDiagnostic' }
      : { kind, name: 'S3 storage', keeps: ['backups', 'exports'], endpoint: '', region: 'us-east-1', bucket: '', prefix: '', pathStyle: true, accessKey: '', secretKey: '' },
  );
  const [tested, setTested] = useState<{ problem: string | null } | null>(null);
  const set = (patch: Partial<StorageLocationRequest>) => {
    setForm((f) => ({ ...f, ...patch }));
    setTested(null);
  };
  const ready =
    form.name.trim().length > 0 &&
    form.keeps.length > 0 &&
    (kind === 'folder' ? (form.path ?? '').trim().length > 0 : (form.bucket ?? '').trim().length > 0 && !!form.accessKey?.trim() && !!form.secretKey?.trim());

  const field = (label: string, key: keyof StorageLocationRequest, props: { placeholder?: string; hint?: string; type?: string; wide?: boolean } = {}) => (
    <label className={cn('flex flex-col gap-1 text-xs', props.wide && 'sm:col-span-2')} htmlFor={`${id}-${key}`}>
      {label}
      <input
        id={`${id}-${key}`}
        type={props.type ?? 'text'}
        className={INPUT}
        spellCheck={false}
        autoComplete={props.type === 'password' ? 'new-password' : 'off'}
        placeholder={props.placeholder}
        value={String(form[key] ?? '')}
        onChange={(e) => set({ [key]: e.target.value })}
      />
      {props.hint && <span className="text-muted">{props.hint}</span>}
    </label>
  );

  return (
    <form
      aria-label={kind === 'folder' ? 'Add a folder' : 'Add an S3 service'}
      className="flex flex-col gap-3 rounded-md border border-accent/40 p-3"
      onSubmit={(e) => {
        e.preventDefault();
        if (ready) change.mutate({ action: 'add', location: form }, { onSuccess: onDone });
      }}
    >
      <p className="text-sm font-medium">{kind === 'folder' ? 'A folder on another drive or a network share' : 'An S3-compatible service (AWS, MinIO, Cloudflare R2, Wasabi, Backblaze B2…)'}</p>
      <div className="grid gap-3 sm:grid-cols-2">
        {field('Name', 'name')}
        {kind === 'folder' ? (
          <>
            {field('Folder', 'path', { placeholder: 'D:\\Backups or \\\\nas\\share', hint: 'A full path.' })}
            {field('Inside it, in', 'prefix', { placeholder: 'PcDiagnostic' })}
          </>
        ) : (
          <>
            {field('Address', 'endpoint', { placeholder: 'https://minio.lan:9000', hint: 'Empty for Amazon S3.' })}
            {field('Region', 'region', { placeholder: 'us-east-1' })}
            {field('Bucket', 'bucket')}
            {field('Folder in the bucket', 'prefix', { placeholder: 'pc-diagnostic/' })}
            <label className="flex items-center gap-2 text-xs sm:col-span-2">
              <input type="checkbox" className="size-4 accent-[var(--accent)]" checked={form.pathStyle ?? true} onChange={(e) => set({ pathStyle: e.target.checked })} />
              The bucket in the path (most services other than AWS)
            </label>
            {field('Access key', 'accessKey')}
            {field('Secret key', 'secretKey', { type: 'password', hint: 'Kept protected on this PC (Windows DPAPI) and never shown again.' })}
          </>
        )}
      </div>
      <div role="group" aria-label="What it keeps" className="flex flex-wrap items-center gap-1.5">
        <span className="mr-1 text-xs text-muted">Keeps</span>
        {kinds.map((k) => {
          const on = form.keeps.includes(k);
          return (
            <button
              key={k}
              type="button"
              aria-pressed={on}
              title={FILE_KINDS[k]?.hint}
              onClick={() => set({ keeps: on ? form.keeps.filter((x) => x !== k) : [...form.keeps, k] })}
              className={cn('rounded-full border px-2.5 py-0.5 text-xs', on ? 'border-accent/50 bg-accent/10 text-accent' : 'border-border text-muted hover:bg-surface-muted')}
            >
              {kindName(k)}
            </button>
          );
        })}
      </div>
      {tested && (
        <p role="status" className={cn('rounded-md border px-3 py-2 text-xs', tested.problem ? 'border-danger/40 bg-danger/10' : 'border-success/40 bg-success/10')}>
          {tested.problem ? `It does not work: ${tested.problem}` : 'It works: a small file was written, read back and deleted there.'}
        </p>
      )}
      <div className="flex flex-wrap gap-2">
        <Button size="sm" type="button" variant="outline" disabled={!ready || test.isPending} onClick={() => test.mutate(form, { onSuccess: setTested, onError: (e) => setTested({ problem: e instanceof Error ? e.message : 'The test failed.' }) })}>
          {test.isPending ? 'Testing…' : 'Test'}
        </Button>
        <Button size="sm" type="submit" disabled={!ready || change.isPending}>
          {change.isPending ? 'Adding…' : 'Add'}
        </Button>
        <Button size="sm" type="button" variant="ghost" onClick={onDone}>
          Cancel
        </Button>
      </div>
    </form>
  );
}

/** "9 Oct, 14:05:12" from "overlay-20261009-140512-345.json" (the time is UTC). */
function backupTime(name: string): Date | null {
  const m = /^overlay-(\d{4})(\d{2})(\d{2})-(\d{2})(\d{2})(\d{2})-(\d{3})\.json$/.exec(name);
  return m ? new Date(Date.UTC(+m[1]!, +m[2]! - 1, +m[3]!, +m[4]!, +m[5]!, +m[6]!, +m[7]!)) : null;
}

/** The on-screen layout's backups, newest first, each one able to be brought back (which keeps the layout it replaces). */
export function LayoutBackups() {
  const [fillRef, fillHeight] = useFillHeight<HTMLUListElement>();
  const backups = useLayoutBackups();
  const restore = useRestoreBackup();
  const [pending, setPending] = useState<PendingAction | null>(null);
  const list = [...(backups.data ?? [])].sort((a, b) => b.name.localeCompare(a.name));
  const label = (name: string) =>
    backupTime(name)?.toLocaleString(undefined, { day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit', second: '2-digit' }) ?? name;

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <History className="size-4" aria-hidden /> Layout backups
        </CardTitle>
        <span className="text-sm tabular-nums text-muted">{list.length > 0 ? `${list.length} kept` : ''}</span>
      </CardHeader>
      <CardContent className="flex flex-col gap-3">
        <p className="text-xs text-muted">
          The on-screen layout as it was before it was changed, kept at most every 5 minutes while you edit (the newest 50), in every location that keeps layout backups.
        </p>
        {backups.error && <p className="text-sm text-danger">The backups could not be read.</p>}
        {backups.data && list.length === 0 && <p className="text-sm text-muted">None yet: the first is kept the next time the layout is saved.</p>}
        {list.length > 0 && (
          <ul ref={fillRef} style={{ maxHeight: fillHeight }} aria-label="Layout backups" className="divide-y divide-border overflow-auto rounded-md border border-border">
            {list.map((b) => (
              <li key={b.name} className="flex items-center gap-3 px-3 py-2">
                <span className="min-w-0 flex-1">
                  <span className="block text-sm tabular-nums">{label(b.name)}</span>
                  <span className="block text-xs text-muted">{formatBytes(b.size)}</span>
                </span>
                <Button
                  size="sm"
                  variant="outline"
                  disabled={restore.isPending}
                  onClick={() =>
                    setPending({
                      title: 'Bring this layout back?',
                      confirmLabel: 'Bring it back',
                      body: <p>The screens show the layout as it was on {label(b.name)}. The layout now is kept as a backup first, so this can be undone.</p>,
                      run: () => restore.mutate(b.name),
                    })
                  }
                >
                  <Undo2 aria-hidden /> Bring back
                </Button>
              </li>
            ))}
          </ul>
        )}
      </CardContent>
      <ConfirmAction pending={pending} onClose={() => setPending(null)} />
    </Card>
  );
}
