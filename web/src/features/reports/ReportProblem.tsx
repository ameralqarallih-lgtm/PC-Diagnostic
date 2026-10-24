import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useRouterState } from '@tanstack/react-router';
import { LifeBuoy, ShieldCheck } from 'lucide-react';
import { useEffect, useId, useState } from 'react';
import { Button } from '@/components/ui/button';
import { Dialog } from '@/components/ui/dialog';
import { useToast } from '@/components/ui/toast';
import { apiGet, apiPost, apiRequest } from '@/lib/api/client';
import type { ProblemReport, ProblemReportEntry, ReportsOverview, SendOutcome } from '@/lib/api/types';

/** The reports and the user's choices. */
export function useReports(enabled = true) {
  return useQuery({ queryKey: ['reports'], queryFn: ({ signal }) => apiGet<ReportsOverview>('/reports', signal), enabled, staleTime: 30_000 });
}

/** Saves the choice about automatic reports. */
export function useConsent() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ automatic, contact }: { automatic: boolean; contact?: string | null }) => apiRequest<ReportsOverview>('PUT', '/reports/consent', { automatic, contact }),
    onSuccess: (overview) => overview && queryClient.setQueryData(['reports'], overview),
  });
}

/** What every report includes, and what never. */
export function WhatIsSent() {
  return (
    <ul className="list-disc pl-5 text-xs text-muted">
      <li>The error and its cause (type, message and where in PC Diagnostic it happened), the version, and which part of PC Diagnostic.</li>
      <li>The log lines that led up to it, and Windows' version, language, processor count and memory.</li>
      <li>Your user name, the PC's name, folders under your profile, e-mail and network addresses are removed first.</li>
      <li>Never: what the recorder recorded about your PC (values, processes, incidents, events) or your files.</li>
    </ul>
  );
}

/** The header button that opens the report dialog. */
export function ReportProblemButton() {
  const [open, setOpen] = useState(false);
  return (
    <>
      <Button variant="ghost" size="icon" aria-label="Report a problem" title="Report a problem with PC Diagnostic" onClick={() => setOpen(true)}>
        <LifeBuoy aria-hidden />
      </Button>
      <ReportProblemDialog open={open} onClose={() => setOpen(false)} />
    </>
  );
}

/**
 * Report a problem with PC Diagnostic to its makers: a line, what happened, optionally the recent log and the
 * hardware summary; the preview shows exactly what is sent. Without a report service in this build the report is
 * kept and can be downloaded to send by e-mail.
 */
export function ReportProblemDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const toast = useToast();
  const queryClient = useQueryClient();
  const page = useRouterState({ select: (s) => s.location.pathname });
  const [summary, setSummary] = useState('');
  const [description, setDescription] = useState('');
  const [contact, setContact] = useState('');
  const [includeLog, setIncludeLog] = useState(true);
  const [includeSystem, setIncludeSystem] = useState(true);
  const [preview, setPreview] = useState<ProblemReport | null>(null);
  const ids = { summary: useId(), description: useId(), contact: useId() };
  const body = { summary, description: description || null, contact: contact || null, includeLog, includeSystem, page };
  useEffect(() => {
    if (open) setPreview(null);
  }, [open]);

  const showPreview = useMutation({ mutationFn: () => apiPost<ProblemReport>('/reports/preview', body), onSuccess: setPreview });
  const send = useMutation({
    mutationFn: () => apiPost<{ report: ProblemReportEntry; outcome: SendOutcome }>('/reports', body),
    onSuccess: ({ report, outcome }) => {
      void queryClient.invalidateQueries({ queryKey: ['reports'] });
      if (outcome.sent) {
        toast.show({ title: 'Report sent. Thank you!', description: outcome.reference ? `Reference: ${outcome.reference}` : undefined, tone: 'success' });
      } else {
        toast.show({ title: 'The report was kept on this PC', description: outcome.problem ?? undefined, tone: 'info' });
        window.open(`/api/v1/reports/${report.id}/download`, '_blank', 'noopener');
      }
      setSummary('');
      setDescription('');
      onClose();
    },
    onError: (error) => toast.show({ title: 'The report could not be written', description: error instanceof Error ? error.message : undefined, tone: 'error' }),
  });
  const valid = summary.trim().length >= 3;

  return (
    <Dialog
      open={open}
      title="Report a problem with PC Diagnostic"
      description="It goes to the makers of PC Diagnostic, to fix it. For problems with your PC itself, capture an incident instead."
      onClose={onClose}
      className="max-w-2xl"
      footer={
        <>
          <Button variant="outline" disabled={!valid || showPreview.isPending} onClick={() => showPreview.mutate()}>
            {preview ? 'Preview again' : 'Preview'}
          </Button>
          <Button disabled={!valid || send.isPending} onClick={() => send.mutate()}>
            {send.isPending ? 'Sending…' : 'Send report'}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-3">
        <div className="flex flex-col gap-1">
          <label htmlFor={ids.summary} className="text-sm font-medium">
            What went wrong?
          </label>
          <input id={ids.summary} value={summary} maxLength={200} onChange={(e) => setSummary(e.target.value)} placeholder="e.g. The History chart stays empty after 6 hours" className="h-9 rounded-md border border-border bg-background px-2 text-sm" />
        </div>
        <div className="flex flex-col gap-1">
          <label htmlFor={ids.description} className="text-sm font-medium">
            What happened, and what were you doing? <span className="font-normal text-muted">(optional)</span>
          </label>
          <textarea id={ids.description} value={description} maxLength={5000} rows={4} onChange={(e) => setDescription(e.target.value)} className="rounded-md border border-border bg-background px-2 py-1.5 text-sm" />
        </div>
        <div className="flex flex-col gap-1">
          <label htmlFor={ids.contact} className="text-sm font-medium">
            Your e-mail, if you want an answer <span className="font-normal text-muted">(optional)</span>
          </label>
          <input id={ids.contact} type="email" value={contact} maxLength={200} onChange={(e) => setContact(e.target.value)} className="h-9 rounded-md border border-border bg-background px-2 text-sm" />
        </div>
        <label className="flex items-start gap-2 text-sm">
          <input type="checkbox" checked={includeLog} onChange={(e) => setIncludeLog(e.target.checked)} className="mt-0.5 size-4 accent-[var(--accent)]" />
          <span>
            Include the recent log of the recorder and the dashboard <span className="block text-xs text-muted">Names, folders and addresses removed. Helps find the cause.</span>
          </span>
        </label>
        <label className="flex items-start gap-2 text-sm">
          <input type="checkbox" checked={includeSystem} onChange={(e) => setIncludeSystem(e.target.checked)} className="mt-0.5 size-4 accent-[var(--accent)]" />
          <span>
            Include the hardware summary <span className="block text-xs text-muted">Processor, graphics and driver, motherboard, memory, drives and Windows version (no serial numbers).</span>
          </span>
        </label>
        {preview && (
          <section aria-label="What will be sent" className="flex flex-col gap-1">
            <p className="text-xs font-medium">Exactly what will be sent:</p>
            <pre className="max-h-72 overflow-auto rounded-md border border-border bg-surface-muted p-2 text-[11px] leading-snug">{JSON.stringify(preview, null, 2)}</pre>
          </section>
        )}
      </div>
    </Dialog>
  );
}

/** Asks once whether PC Diagnostic may send the problems it catches by itself. */
export function ReportingConsentBanner() {
  const reports = useReports();
  const consent = useConsent();
  const [details, setDetails] = useState(false);
  if (!reports.data || reports.data.askedAt || !reports.data.serviceConfigured) return null;
  return (
    <div role="region" aria-label="Problem reports" className="mb-4 flex flex-col gap-2 rounded-lg border border-accent/40 bg-accent/5 px-3 py-2.5 text-sm">
      <p className="flex items-start gap-2">
        <ShieldCheck className="mt-0.5 size-4 shrink-0 text-accent" aria-hidden />
        <span>
          <span className="font-medium">Help fix problems in PC Diagnostic faster?</span> When something in PC Diagnostic itself fails, it can send its makers a report of the error and its cause.{' '}
          <button type="button" className="text-accent hover:underline" onClick={() => setDetails((d) => !d)}>
            {details ? 'Hide what is sent' : 'What is sent?'}
          </button>
        </span>
      </p>
      {details && <WhatIsSent />}
      <div className="flex flex-wrap gap-2">
        <Button size="sm" onClick={() => consent.mutate({ automatic: true })}>
          Send reports automatically
        </Button>
        <Button size="sm" variant="outline" onClick={() => consent.mutate({ automatic: false })}>
          Not now
        </Button>
      </div>
    </div>
  );
}

let installed = false;

/** Reports errors the dashboard runs into in the browser (kept like the others; sent only with consent). */
export function installClientErrorReporting() {
  if (installed || typeof window === 'undefined') return;
  installed = true;
  let sentThisMinute = 0;
  window.setInterval(() => (sentThisMinute = 0), 60_000);
  const report = (message: string, stack: string | undefined, component: string) => {
    if (sentThisMinute++ >= 5 || !message) return;
    void fetch('/api/v1/reports/client-error', {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ message: message.slice(0, 2000), stack: stack?.slice(0, 20_000), page: window.location.pathname, component }),
    }).catch(() => {});
  };
  window.addEventListener('error', (event) => report(event.message, event.error instanceof Error ? event.error.stack : undefined, 'script'));
  window.addEventListener('unhandledrejection', (event) => {
    const reason = event.reason as unknown;
    if (reason instanceof Error && reason.name === 'AbortError') return;
    report(reason instanceof Error ? reason.message : String(reason), reason instanceof Error ? reason.stack : undefined, 'promise');
  });
}
