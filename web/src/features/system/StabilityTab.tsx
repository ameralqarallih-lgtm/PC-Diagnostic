import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { DataTable, type Column } from '@/components/data/DataTable';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { useConfirm } from '@/components/ui/confirm';
import { useToast } from '@/components/ui/toast';
import { apiGet, apiPost } from '@/lib/api/client';
import type { CrashKind, CrashRecord, StabilityResponse } from '@/lib/api/types';
import { formatBytes, formatDateTime } from '@/lib/format';

const DAYS = 30;

/** How many dumps the button asks Windows to keep. */
const KEEP = 50;

const KIND_TEXT: Record<CrashKind, string> = {
  BlueScreen: 'Blue screen',
  PowerButton: 'Froze, turned off with the power button',
  Unexpected: 'Went down without a blue screen',
};

const COLUMNS: Column<CrashRecord>[] = [
  { id: 'when', header: 'When', value: (c) => Date.parse(c.restarted), groupable: false, firstDesc: true },
  { id: 'kind', header: 'What happened', value: (c) => KIND_TEXT[c.kind] },
  { id: 'code', header: 'Bug check', value: (c) => (c.bugCheck ? `${c.bugCheck} ${c.bugCheckName ?? ''}`.trim() : 'none') },
  { id: 'module', header: 'Where', value: (c) => c.module ?? (c.bugCheck ? 'not known' : 'no dump') },
  { id: 'incident', header: 'Incident', value: (c) => (c.incidentKey ? 'Has one' : 'None'), firstDesc: false },
];

/**
 * How stable the PC has been, from Windows' own records: every time it went down without shutting down (blue screens with
 * their code and where the dump points, freezes ended with the power button, other unexpected shutdowns), PC Diagnostic's
 * incident for each, how many crash dumps Windows keeps, and Windows Memory Diagnostic's results.
 */
export function StabilityTab() {
  const queryClient = useQueryClient();
  const confirm = useConfirm();
  const toast = useToast();
  const { data, error } = useQuery({
    queryKey: ['stability', DAYS],
    queryFn: ({ signal }) => apiGet<StabilityResponse>(`/stability?days=${DAYS}`, signal),
    refetchInterval: 5 * 60_000,
  });
  const done = (message: string) => (answer: StabilityResponse) => {
    if (answer.problem) toast.show({ title: answer.problem, tone: 'error' });
    else toast.show({ title: message, tone: 'success' });
    if (answer.report) queryClient.setQueryData(['stability', DAYS], answer);
  };
  const keep = useMutation({ mutationFn: () => apiPost<StabilityResponse>('/stability/dumps', { keep: KEEP }), onSuccess: done(`Windows now keeps the last ${KEEP} crash dumps.`) });
  const test = useMutation({ mutationFn: () => apiPost<StabilityResponse>('/stability/memory-test', {}), onSuccess: done('Windows Memory Diagnostic is open on this PC.') });

  if (error) return <p className="text-sm text-danger">The crash records could not be loaded.</p>;
  if (!data) return <p className="text-sm text-muted">Loading…</p>;
  if (!data.report) return <p className="text-sm text-muted">{data.problem}</p>;

  const report = data.report;
  const count = (kind: CrashKind) => report.crashes.filter((c) => c.kind === kind).length;
  const dumps = report.dumps;
  return (
    <div className="flex flex-col gap-4">
      <Card>
        <CardHeader>
          <CardTitle>Crashes in the last {DAYS} days</CardTitle>
        </CardHeader>
        <CardContent className="flex flex-col gap-3">
          <p className="text-sm" role="status">
            {report.crashes.length === 0
              ? `Windows did not go down unexpectedly in the last ${DAYS} days.`
              : `Windows went down without shutting down ${report.crashes.length} time${report.crashes.length === 1 ? '' : 's'}: ${count('BlueScreen')} blue screen${count('BlueScreen') === 1 ? '' : 's'}, ${count('PowerButton')} freeze${count('PowerButton') === 1 ? '' : 's'} ended with the power button, ${count('Unexpected')} other.`}
            {report.logStarts && <span className="text-muted"> Windows&apos; log reaches back only to {formatDateTime(report.logStarts)}.</span>}
          </p>
          {report.problem && <p className="text-sm text-danger">{report.problem}</p>}
          {report.crashes.length > 0 && (
            <DataTable
              viewKey="stability-crashes"
              label="Crashes"
              rows={report.crashes}
              columns={COLUMNS}
              rowKey={(c) => c.restarted}
              defaultView={{ groupBy: [], sort: [{ id: 'when', desc: true }] }}
              listClassName="gap-2"
              renderRow={(c) => <CrashRow crash={c} />}
            />
          )}
        </CardContent>
      </Card>

      <div className="grid gap-4 lg:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle>Crash dumps</CardTitle>
          </CardHeader>
          <CardContent className="flex flex-col gap-3 text-sm">
            {dumps ? (
              <>
                <p>
                  Windows keeps the last {dumps.kept} small crash dumps{dumps.set ? '' : ' (its default)'}; {dumps.files} {dumps.files === 1 ? 'is' : 'are'} in {dumps.folder} now ({formatBytes(dumps.bytes)}). Older ones are deleted, and with them what they say about the crash.
                </p>
                {dumps.kept < KEEP && (
                  <Button
                    className="self-start"
                    variant="outline"
                    disabled={keep.isPending}
                    onClick={async () =>
                      (await confirm({ title: `Keep the last ${KEEP} crash dumps?`, description: `Each takes a few hundred KB to a few dozen MB in ${dumps.folder}. Windows deletes the oldest beyond ${KEEP}.`, action: `Keep ${KEEP}` })) &&
                      keep.mutate()
                    }
                  >
                    Keep the last {KEEP}
                  </Button>
                )}
              </>
            ) : (
              <p className="text-muted">The dump setting could not be read: the recorder needs to run as administrator.</p>
            )}
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>Memory test</CardTitle>
          </CardHeader>
          <CardContent className="flex flex-col gap-3 text-sm">
            {report.memoryTests.length === 0 ? (
              <p>Windows Memory Diagnostic has not run here in the time Windows&apos; log covers. Faulty memory causes crashes with different codes each time, and freezes.</p>
            ) : (
              <ul className="flex flex-col gap-1">
                {report.memoryTests.map((t) => (
                  <li key={t.at} className={t.errorsFound ? 'text-danger' : undefined}>
                    {formatDateTime(t.at)}: {t.message}
                  </li>
                ))}
              </ul>
            )}
            <Button
              className="self-start"
              variant="outline"
              disabled={test.isPending}
              onClick={async () =>
                (await confirm({
                  title: 'Test the memory?',
                  description:
                    'Windows Memory Diagnostic opens on this PC: choose to restart now, or to check the next time Windows starts. The test runs before Windows starts and takes 15 minutes to an hour; the result shows here afterwards.',
                  action: 'Open it',
                })) && test.mutate()
              }
            >
              Test the memory…
            </Button>
          </CardContent>
        </Card>
      </div>
    </div>
  );
}

function CrashRow({ crash }: { crash: CrashRecord }) {
  return (
    <div className="mb-2 flex flex-col gap-1 rounded-lg border border-border bg-surface p-3 text-sm">
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <span className="font-medium">
          {KIND_TEXT[crash.kind]}
          {crash.bugCheck && (
            <span className="ml-1 font-mono text-xs text-muted">
              {crash.bugCheck}
              {crash.bugCheckName ? ` ${crash.bugCheckName}` : ''}
            </span>
          )}
        </span>
        {crash.module && <Badge tone="warning">in {crash.module}</Badge>}
      </div>
      <span className="text-xs text-muted">
        {crash.wentDown ? `Went down ${formatDateTime(crash.wentDown)}, back ${formatDateTime(crash.restarted)}` : `Back ${formatDateTime(crash.restarted)}`}
        {crash.dumpFile && ` · dump ${crash.dumpFile.split('\\').pop()}${crash.dumpKept ? '' : ' (deleted by Windows)'}`}
      </span>
      <span className="text-muted">{crash.explanation}</span>
      {crash.incidentKey ? (
        <Link to="/incidents/$key" params={{ key: crash.incidentKey }} className="self-start text-xs text-accent hover:underline">
          Its incident {crash.incidentKey}
        </Link>
      ) : (
        <span className="text-xs text-muted">No incident: PC Diagnostic did not record this one.</span>
      )}
    </div>
  );
}
