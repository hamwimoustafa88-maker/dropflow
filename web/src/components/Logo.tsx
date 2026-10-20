// DropFlow mark: a six-segment wheel with a drop in the centre. Original artwork.
export function LogoMark({ size = 28 }: { size?: number }) {
  const segments = Array.from({ length: 6 }, (_, i) => {
    const a0 = ((i * 60 - 90 + 4) * Math.PI) / 180;
    const a1 = (((i + 1) * 60 - 90 - 4) * Math.PI) / 180;
    const r0 = 9;
    const r1 = 22;
    const p = (r: number, a: number) => `${(24 + r * Math.cos(a)).toFixed(2)} ${(24 + r * Math.sin(a)).toFixed(2)}`;
    return `M ${p(r0, a0)} L ${p(r1, a0)} A ${r1} ${r1} 0 0 1 ${p(r1, a1)} L ${p(r0, a1)} A ${r0} ${r0} 0 0 0 ${p(r0, a0)} Z`;
  });
  return (
    <svg width={size} height={size} viewBox="0 0 48 48" aria-hidden="true" focusable="false">
      {segments.map((d, i) => (
        <path key={i} d={d} fill={i === 0 ? "var(--accent)" : "var(--primary)"} opacity={i === 0 ? 1 : 0.9} />
      ))}
      <path d="M24 17c3 3.6 4.5 5.8 4.5 7.8a4.5 4.5 0 0 1-9 0c0-2 1.5-4.2 4.5-7.8Z" fill="var(--bg)" />
    </svg>
  );
}

export function Logo() {
  return (
    <span className="inline-flex items-center gap-2.5 font-bold tracking-tight text-lg">
      <LogoMark />
      <span>DropFlow</span>
    </span>
  );
}
