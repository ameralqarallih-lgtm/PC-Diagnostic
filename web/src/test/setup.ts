import '@testing-library/jest-dom/vitest';
import { cleanup } from '@testing-library/react';
import { createElement } from 'react';
import { afterEach, vi } from 'vitest';

// jsdom has no layout; the router's scroll restoration and list auto-scroll call these.
window.scrollTo = () => {};
Element.prototype.scrollIntoView = () => {};

// jsdom has no canvas: charts render as a labelled placeholder that exposes the option for assertions.
vi.mock('@/components/charts/EChart', () => ({
  EChart: ({ label, option }: { label: string; option: unknown }) =>
    createElement('div', { role: 'img', 'aria-label': label, 'data-option': JSON.stringify(option) }),
}));

afterEach(() => cleanup());
