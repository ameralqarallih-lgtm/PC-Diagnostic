import { cva, type VariantProps } from 'class-variance-authority';
import type { ComponentProps } from 'react';
import { cn } from '@/lib/utils';

const badgeVariants = cva('inline-flex items-center gap-1.5 rounded-full border px-2.5 py-0.5 text-xs font-medium', {
  variants: {
    tone: {
      neutral: 'border-border text-muted',
      success: 'border-success/40 text-success',
      warning: 'border-warning/40 text-warning',
      danger: 'border-danger/40 text-danger',
      accent: 'border-accent/40 text-accent',
    },
  },
  defaultVariants: { tone: 'neutral' },
});

export interface BadgeProps extends ComponentProps<'span'>, VariantProps<typeof badgeVariants> {}

export function Badge({ className, tone, ...props }: BadgeProps) {
  return <span className={cn(badgeVariants({ tone }), className)} {...props} />;
}
