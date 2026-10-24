import { Card, CardContent } from '@/components/ui/card';

/** Stand-in for pages that are built in later phases. States plainly what is missing and when it arrives. */
export function PhasePlaceholder({ title, phase, description }: { title: string; phase: string; description: string }) {
  return (
    <div className="flex flex-col gap-4">
      <h1 className="text-xl font-semibold">{title}</h1>
      <Card>
        <CardContent className="pt-4">
          <p className="text-sm">{description}</p>
          <p className="mt-2 text-sm text-muted">Planned for {phase}.</p>
        </CardContent>
      </Card>
    </div>
  );
}
