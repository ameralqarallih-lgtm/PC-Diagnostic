import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Download, LifeBuoy, MoreHorizontal } from 'lucide-react';
import { useState } from 'react';
import { DataTable, type Column } from '@/components/data/DataTable';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { useConfirm } from '@/components/ui/confirm';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Menu } from '@/components/ui/menu';
import { useToast } from '@/components/ui/toast';
import { ReportProblemDialog, useConsent, useReports, WhatIsSent } from '@/features/reports/ReportProblem';
import { apiPost, apiRequest } from '@/lib/api/client';
import type { ProblemReportEntry, SendOutcome } from '@/lib/api/types';
import { formatDateTime } from '@/lib/format';

const STATUS: Record<ProblemReportEntry['status'], { text: string; tone: 'success' | 'warning' | 'neutral' | 'accent' }> = {
  sent: { text: 'Sent', tone: 'success' },
  pending: { text: 'Waiting to be sent', tone: 'accent' },
  failed: { text: 'Not sent yet', tone: 'warning' },
  local: { text: 'Kept on this PC', tone: 'neutral' },
};

/** Problem reports to the makers: the consent to automatic reports, writing one, and the reports so far. */
export function ReportsSection() {
  const reports = useReports();
  const consent = useConsent();
  const toast = useToast();
  const queryClient = useQueryClient();
  const [writing, setWriting] = useState(false);
  const refresh = () => void queryClient.invalidateQueries({ queryKey: ['reports'] });
  const send = useMutation({
    mutationFn: (id: string) => apiPost<SendOutcome>(`/reports/${id}/send`),
    onSuccess: (outcome) => {
      refresh();
      toast.show(outcome.sent ? { title: 'Report sent', description: outcome.reference ?? undefined, tone: 'success' } : { title: 'Not sent', description: outcome.problem ?? undefined, tone: 'error' });
    },
  });
  const remove = useMutation({ mutationFn: (id: string) => apiRequest('DELETE', `/reports/${id}`), onSuccess: refresh });
  const confirm = useConfirm();
  const data = reports.data;

  const columns: Column<ProblemReportEntry>[] = [
    { id: 'summary', header: 'Problem', value: (r) => r.summary, groupable: false, className: 'max-w-96 truncate', cell: (r) => <span title={r.summary}>{r.summary}</span> },
    { id: 'kind', header: 'Kind', value: (r) => (r.kind === 'manual' ? 'Written by you' : 'Caught automatically') },
    { id: 'status', header: 'Status', value: (r) => STATUS[r.status].text, cell: (r) => <Badge tone={STATUS[r.status].tone} title={r.lastError ?? r.reference ?? undefined}>{STATUS[r.status].text}</Badge> },
    { id: 'times', header: 'Times', value: (r) => r.occurrences, groupable: false, align: 'right', firstDesc: true },
    { id: 'at', header: 'When', value: (r) => Date.parse(r.at), groupable: false, firstDesc: true, cell: (r) => formatDateTime(r.at), className: 'text-xs text-muted' },
    { id: 'component', header: 'Part', value: (r) => r.component.split('/')[0] ?? r.component, className: 'text-xs text-muted' },
    {
      id: 'actions',
      header: '',
      value: () => null,
      sortable: false,
      groupable: false,
      className: 'w-8',
      cell: (r) => (
        <Menu
          label={`Actions for ${r.summary}`}
          items={[
            { label: 'Send now', onSelect: () => send.mutate(r.id), disabled: r.status === 'sent' || !data?.serviceConfigured },
            { label: 'Download', onSelect: () => window.open(`/api/v1/reports/${r.id}/download`, '_blank', 'noopener') },
            { label: 'Delete', onSelect: async () => (await confirm({ title: 'Delete this report?', description: 'It is deleted from this PC.' })) && remove.mutate(r.id), danger: true },
          ]}
        >
          <MoreHorizontal aria-hidden />
        </Menu>
      ),
    },
  ];

  return (
    <Card id="reports" className="scroll-mt-28">
      <CardHeader className="flex-col items-start gap-1">
        <CardTitle className="flex items-center gap-2">
          <LifeBuoy className="size-4 text-accent" aria-hidden /> Problem reports
        </CardTitle>
        <p className="text-sm text-muted">When something in PC Diagnostic itself fails, a report of the error and its cause helps its makers fix it.</p>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        {!data ? (
          <p className="text-sm text-muted">Loading…</p>
        ) : (
          <>
            <label className="flex cursor-pointer items-start justify-between gap-6">
              <span>
                <span className="text-sm font-medium">Send problem reports automatically</span>
                <span className="block text-xs text-muted">
                  {data.serviceConfigured
                    ? 'Errors PC Diagnostic catches by itself are sent in the background. Off: they are only kept here, and you decide which to send.'
                    : 'This build has no report service configured: reports are kept here; download one to send it to support.'}
                </span>
              </span>
              <span className="relative mt-0.5 inline-flex shrink-0">
                <input type="checkbox" checked={data.automatic} disabled={!data.serviceConfigured} aria-label="Send problem reports automatically" onChange={(e) => consent.mutate({ automatic: e.target.checked, contact: data.contact })} className="peer sr-only" />
                <span aria-hidden className="h-5 w-9 rounded-full bg-border transition-colors peer-checked:bg-accent peer-disabled:opacity-50" />
                <span aria-hidden className="absolute left-0.5 top-0.5 size-4 rounded-full bg-surface shadow transition-transform peer-checked:translate-x-4" />
              </span>
            </label>
            <WhatIsSent />
            <div className="flex flex-wrap gap-2">
              <Button type="button" variant="outline" size="sm" onClick={() => setWriting(true)}>
                <LifeBuoy aria-hidden /> Report a problem
              </Button>
              {data.items.length > 0 && (
                <Button type="button" variant="ghost" size="sm" onClick={() => window.open(`/api/v1/reports/${data.items[0]!.id}/download`, '_blank', 'noopener')}>
                  <Download aria-hidden /> Download the latest
                </Button>
              )}
            </div>
            <DataTable
              viewKey="settings.reports"
              label="Problem reports"
              rows={data.items}
              columns={columns}
              rowKey={(r) => r.id}
              defaultView={{ groupBy: [], sort: [{ id: 'at', desc: true }] }}
              emptyText="No problems so far."
              compact
              fill
            />
          </>
        )}
      </CardContent>
      <ReportProblemDialog open={writing} onClose={() => setWriting(false)} />
    </Card>
  );
}
