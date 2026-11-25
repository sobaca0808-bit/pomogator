import foundation from './assets/wifi-brand-base.png'

/** Preserve the family mark and replace only the Wi-Fi product badge. */
export function BrandMark({ size }: { size: number }) {
  return <svg width={size} height={size} viewBox="0 0 256 256" aria-hidden="true" focusable="false">
    <image href={foundation} width="256" height="256" />
    <rect x="35" y="165" width="186" height="57" rx="28.5" fill="#102337" stroke="#58eaa0" strokeWidth="4" />
    <text x="128" y="201" fill="#75f0af" fontFamily="system-ui, sans-serif" fontWeight="800"
      fontSize="24" textAnchor="middle" textLength="158" lengthAdjust="spacingAndGlyphs">ПОМОГАТОР</text>
  </svg>
}
