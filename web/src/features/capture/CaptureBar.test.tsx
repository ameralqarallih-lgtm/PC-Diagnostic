import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { USER_INCIDENT_TYPES } from '@/lib/api/types';
import { CaptureBar } from './CaptureBar';

describe('CaptureBar', () => {
  it('offers every quick incident type', () => {
    render(<CaptureBar canCapture />);

    expect(screen.getByRole('button', { name: /capture incident/i })).toBeEnabled();
    for (const type of USER_INCIDENT_TYPES) {
      expect(screen.getByRole('button', { name: type.label })).toBeInTheDocument();
    }
  });

  it('reports the chosen type, or null for the main button', async () => {
    const onCapture = vi.fn();
    render(<CaptureBar canCapture onCapture={onCapture} />);

    await userEvent.click(screen.getByRole('button', { name: 'Screen Flicker' }));
    await userEvent.click(screen.getByRole('button', { name: /capture incident/i }));

    expect(onCapture).toHaveBeenNthCalledWith(1, 'ScreenFlicker', '');
    expect(onCapture).toHaveBeenNthCalledWith(2, null, '');
  });

  it('sends the note with the next capture and then clears it', async () => {
    const onCapture = vi.fn();
    render(<CaptureBar canCapture onCapture={onCapture} />);

    const note = screen.getByLabelText(/note/i);
    await userEvent.type(note, 'black screen in game');
    await userEvent.click(screen.getByRole('button', { name: 'Game Crash' }));

    expect(onCapture).toHaveBeenCalledWith('GameCrash', 'black screen in game');
    expect(note).toHaveValue('');
  });

  it('cannot be pressed twice while a capture is in progress', () => {
    render(<CaptureBar canCapture busy />);

    expect(screen.getByRole('button', { name: /capture incident/i })).toBeDisabled();
  });

  it('is disabled and explains why when the recorder is not connected', () => {
    render(<CaptureBar canCapture={false} unavailableReason="Recorder not connected." />);

    const capture = screen.getByRole('button', { name: /capture incident/i });
    expect(capture).toBeDisabled();
    expect(capture).toHaveAccessibleDescription('Recorder not connected.');
    expect(screen.getByRole('button', { name: 'Game Crash' })).toBeDisabled();
  });
});
