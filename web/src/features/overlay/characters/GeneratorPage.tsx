import { Link } from '@tanstack/react-router';
import { useId, useState } from 'react';
import { Card, CardContent, CardHeader } from '@/components/ui/card';
import { TabFade, TabPill } from '@/components/ui/tab-indicator';
import { cn } from '@/lib/utils';
import { AnimationsSection } from './AnimationsSection';
import { Generations, GeneratorCard, GeneratorState, PromptForm } from './CharacterGenerate';
import { useGenerator } from './generation';

const TABS = [
  ['characters', 'Characters'],
  ['animations', 'Animations'],
  ['emotions', 'Emotions'],
] as const;

type Tab = (typeof TABS)[number][0];

/**
 * The character generator's own page (UI v2), a tab for each thing it makes: a new character asked for and every
 * character being made, each with its stages (drafts, animating, in the library); new animations; and new emotions
 * (each for every character, managed per character). Beside it, the generator's setup and its service (start, stop).
 * It fits the window: each column scrolls by itself.
 */
export function GeneratorPage() {
  const generator = useGenerator();
  const off = generator.data?.kind === 'off';
  const [tab, setTab] = useState<Tab>('characters');
  const group = useId();
  return (
    <div className="flex flex-col gap-3 xl:h-[calc(100dvh-6.5rem)] xl:overflow-hidden">
      <header className="flex flex-wrap items-baseline gap-x-4 gap-y-1">
        <h1 className="text-xl font-semibold">Character generator</h1>
        <p className="text-sm text-muted">
          Describe a character; it is drawn, you choose a draft, and it is animated into the{' '}
          <Link to="/characters" className="text-accent hover:underline">
            Characters
          </Link>{' '}
          library.
        </p>
      </header>
      <div className="grid min-h-0 flex-1 grid-cols-1 gap-3 xl:grid-cols-[minmax(0,1fr)_30rem]">
        <Card className="flex min-h-0 flex-col">
          <CardHeader>
            <div role="tablist" aria-label="What the generator makes" className="flex gap-1 rounded-lg bg-surface-muted p-1 text-sm">
              {TABS.map(([id, name]) => (
                <button key={id} type="button" role="tab" aria-selected={tab === id} onClick={() => setTab(id)} className={cn('relative isolate flex-1 rounded-md px-3 py-1', tab === id ? 'font-medium' : 'text-muted hover:text-foreground')}>
                  {tab === id && <TabPill group={group} />}
                  {name}
                </button>
              ))}
            </div>
          </CardHeader>
          <CardContent className="flex min-h-0 flex-1 flex-col gap-4 overflow-y-auto text-sm">
            <TabFade tab={tab} className="flex flex-col gap-4">
              {generator.data && off ? (
                <p className="text-muted">Set up a generator first (beside this), on this PC or another service.</p>
              ) : (
                <GeneratorState onSetup={() => document.getElementById('character-generator')?.scrollIntoView?.({ behavior: 'smooth' })} />
              )}
              {tab === 'characters' && (
                <>
                  {!(generator.data && off) && <PromptForm onSetup={() => undefined} />}
                  <Generations />
                </>
              )}
              {tab !== 'characters' && <AnimationsSection kind={tab === 'emotions' ? 'emotion' : 'animation'} />}
            </TabFade>
          </CardContent>
        </Card>
        <div className="min-h-0 overflow-y-auto">
          <GeneratorCard />
        </div>
      </div>
    </div>
  );
}
