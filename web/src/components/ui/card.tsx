import { createContext, useContext, useId, type ComponentProps } from 'react';
import { cn } from '@/lib/utils';

// The card's title labels its <section>, so each card is a named region for screen readers.
const CardTitleId = createContext<string | undefined>(undefined);

export function Card({ className, ...props }: ComponentProps<'section'>) {
  const titleId = useId();
  return (
    <CardTitleId.Provider value={titleId}>
      <section aria-labelledby={titleId} className={cn('rounded-lg border border-border bg-surface', className)} {...props} />
    </CardTitleId.Provider>
  );
}

export function CardHeader({ className, ...props }: ComponentProps<'header'>) {
  return <header className={cn('flex items-center justify-between gap-2 px-4 pt-4', className)} {...props} />;
}

export function CardTitle({ className, ...props }: ComponentProps<'h2'>) {
  const id = useContext(CardTitleId);
  return <h2 id={id} className={cn('text-sm font-semibold uppercase tracking-wide text-muted', className)} {...props} />;
}

export function CardContent({ className, ...props }: ComponentProps<'div'>) {
  return <div className={cn('px-4 pb-4 pt-3', className)} {...props} />;
}
