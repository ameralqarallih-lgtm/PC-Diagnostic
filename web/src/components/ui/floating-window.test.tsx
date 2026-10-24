import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { FloatingWindow } from './floating-window';

afterEach(() => window.localStorage.clear());

describe('FloatingWindow', () => {
  it('opens where it was left, kept on the screen, and closes with Escape', async () => {
    // Stored while the screen was larger: brought back onto this one.
    window.localStorage.setItem('test.window', JSON.stringify({ x: 5000, y: -40, width: 640, height: 480 }));
    const onClose = vi.fn();
    render(
      <FloatingWindow open title="This moment" onClose={onClose} storageKey="test.window">
        <p>content</p>
      </FloatingWindow>,
    );

    const window_ = screen.getByRole('dialog', { name: 'This moment' });
    expect(window_).toHaveAttribute('aria-modal', 'false');
    expect(window_.style.width).toBe('640px');
    expect(parseFloat(window_.style.left)).toBeLessThanOrEqual(window.innerWidth - 640);
    expect(parseFloat(window_.style.top)).toBeGreaterThanOrEqual(0);
    expect(screen.getByText('content')).toBeInTheDocument();

    await userEvent.keyboard('{Escape}');
    expect(onClose).toHaveBeenCalled();
  });

  /** Regression: inside a dashboard widget (transformed, contained) a fixed window was placed and clipped by the widget. */
  it('is rendered on the page body, outside any transformed container', () => {
    render(
      <div data-testid="widget" style={{ transform: 'translate(40px, 40px)', containerType: 'inline-size', overflow: 'hidden' }}>
        <FloatingWindow open title="This moment" onClose={() => {}} storageKey="test.window">
          <p>content</p>
        </FloatingWindow>
      </div>,
    );

    const window_ = screen.getByRole('dialog', { name: 'This moment' });
    expect(window_.parentElement).toBe(document.body);
    expect(screen.getByTestId('widget')).not.toContainElement(window_);
  });

  it('is not rendered while closed', () => {
    render(
      <FloatingWindow open={false} title="This moment" onClose={() => {}} storageKey="test.window">
        <p>content</p>
      </FloatingWindow>,
    );
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });
});
