import qrcode from 'qrcode-generator';
import { useMemo } from 'react';
import { cn } from '@/lib/utils';

/** A QR code drawn as one SVG path: dark squares on white with a quiet zone, which phone cameras read best. */
export function QrCode({ text, size = 184, label, className }: { text: string; size?: number; label: string; className?: string }) {
  const { count, path } = useMemo(() => {
    const qr = qrcode(0, 'M');
    qr.addData(text);
    qr.make();
    const n = qr.getModuleCount();
    let d = '';
    for (let row = 0; row < n; row++) {
      for (let col = 0; col < n; col++) {
        if (qr.isDark(row, col)) d += `M${col} ${row}h1v1h-1z`;
      }
    }
    return { count: n, path: d };
  }, [text]);
  const quiet = 2;

  return (
    <svg
      viewBox={`${-quiet} ${-quiet} ${count + 2 * quiet} ${count + 2 * quiet}`}
      width={size}
      height={size}
      shapeRendering="crispEdges"
      role="img"
      aria-label={label}
      data-qr={text}
      className={cn('rounded-lg bg-white', className)}
    >
      <rect x={-quiet} y={-quiet} width={count + 2 * quiet} height={count + 2 * quiet} fill="#fff" />
      <path d={path} fill="#000" />
    </svg>
  );
}
