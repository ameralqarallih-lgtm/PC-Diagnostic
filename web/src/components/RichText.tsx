import { Link } from '@tanstack/react-router';
import { Clock } from 'lucide-react';
import type { ReactNode } from 'react';
import { cn } from '@/lib/utils';

/** A piece of a sentence: plain text, or something worth seeing at a glance. */
export type Token =
  | { kind: 'text'; text: string }
  | { kind: 'time'; text: string }
  | { kind: 'number'; text: string }
  | { kind: 'incident'; text: string }
  | { kind: 'code'; text: string };

// Most specific first: a date and time (with UTC), a date, a time; an incident key; an event id; a program or file; a
// quoted name; a number with its unit (or a percentage, a count, a size, a duration); a number of 2+ digits or with decimals.
const PATTERN = new RegExp(
  [
    String.raw`(?<time>\b\d{4}-\d{2}-\d{2}(?:[ T]\d{1,2}:\d{2}(?::\d{2})?(?:\.\d+)?)?(?:\s?(?:UTC|Z))?|\b\d{1,2}:\d{2}(?::\d{2})?(?:\s?(?:UTC|AM|PM))?\b)`,
    String.raw`(?<incident>\bINC-\d{8}-\d{5}\b)`,
    String.raw`(?<code>\b(?:[Ee]vent(?:\s+ID)?|ID)\s+\d{1,5}\b|\b[\w.-]+\.(?:exe|dll|sys|inf|cat)\b|"[^"\n]{1,60}"|“[^”\n]{1,60}”)`,
    String.raw`(?<number>[−-]?\b\d[\d,]*(?:\.\d+)?\s?(?:%|°C|°F|[KMGT]i?B(?:\/s)?|B\/s|bytes?|ms|µs|s|min|minutes?|h|hours?|days?|MHz|GHz|W|V|RPM|fps|Mbit\/s|Gbit\/s|×)(?![\w])|\b\d+(?:\.\d+)\b|\b\d{2,}(?:,\d{3})*\b)`,
  ].join('|'),
  'g',
);

/** Splits a sentence into plain text and the times, numbers, incidents and names in it. */
export function tokenize(text: string): Token[] {
  const tokens: Token[] = [];
  let last = 0;
  for (const m of text.matchAll(PATTERN)) {
    const at = m.index ?? 0;
    if (at > last) tokens.push({ kind: 'text', text: text.slice(last, at) });
    const groups = m.groups ?? {};
    const kind = groups.time ? 'time' : groups.incident ? 'incident' : groups.code ? 'code' : 'number';
    tokens.push({ kind, text: m[0] });
    last = at + m[0].length;
  }
  if (last < text.length) tokens.push({ kind: 'text', text: text.slice(last) });
  return tokens;
}

/**
 * How a report sentence reads best: its lead (before ": ", when there is a clear one) and its parts (split at "; "),
 * so "14 unclean stops…, the last at 19:43:09: It stopped…; Windows kept running." becomes a headline and two items.
 */
export function structure(text: string): { lead: string | null; parts: string[] } {
  const colon = text.search(/:\s(?=[A-Z“"(])/);
  const lead = colon > 0 && colon < 160 ? text.slice(0, colon) : null;
  const rest = lead ? text.slice(colon + 2) : text;
  const parts = rest
    .split(/;\s+/)
    .map((p) => p.trim())
    .filter(Boolean);
  return { lead, parts };
}

/** A sentence with its times, numbers, incidents and names marked, in the app's own styles. */
export function Marked({ text, className }: { text: string; className?: string }) {
  return <span className={className}>{tokenize(text).map((t, i) => <Piece key={i} token={t} />)}</span>;
}

function Piece({ token }: { token: Token }): ReactNode {
  switch (token.kind) {
    case 'time':
      return (
        <span className="inline-flex items-baseline gap-0.5 whitespace-nowrap rounded bg-surface-muted px-1 font-mono text-[0.92em] tabular-nums text-foreground">
          <Clock className="relative top-[1px] size-3 self-center text-muted" aria-hidden />
          {token.text}
        </span>
      );
    case 'number':
      return <span className="whitespace-nowrap font-semibold tabular-nums text-foreground">{token.text}</span>;
    case 'incident':
      return (
        <Link to="/incidents/$key" params={{ key: token.text }} search={{}} className="font-mono text-[0.92em] text-accent hover:underline">
          {token.text}
        </Link>
      );
    case 'code':
      return <code className="rounded bg-surface-muted px-1 font-mono text-[0.9em] text-foreground">{token.text}</code>;
    default:
      return token.text;
  }
}

/**
 * Report text made easy to scan: a headline and items when the sentence has them, otherwise the sentence, with its
 * times, figures, incidents and names marked.
 */
export function RichText({ text, className, prefix }: { text: string; className?: string; prefix?: ReactNode }) {
  const { lead, parts } = structure(text);
  if (!lead && parts.length <= 1) {
    return (
      <p className={cn('text-sm', className)}>
        {prefix}
        <Marked text={text} />
      </p>
    );
  }
  return (
    <div className={cn('flex flex-col gap-1 text-sm', className)}>
      <p>
        {prefix}
        {lead ? <Marked text={lead} className="font-medium" /> : <Marked text={parts[0]!} />}
      </p>
      <ul className="flex flex-col gap-0.5 pl-1">
        {(lead ? parts : parts.slice(1)).map((p, i) => (
          <li key={i} className="flex gap-2">
            <span className="mt-[0.55em] size-1 shrink-0 rounded-full bg-muted" aria-hidden />
            <Marked text={p} className="text-muted [&>span]:text-foreground" />
          </li>
        ))}
      </ul>
    </div>
  );
}
