const tok = n => `rgb(var(--${n}) / <alpha-value>)`;
module.exports = {
  content: ['./web/index.html', './web/app.js', '../wwwroot/app/index.html', '../wwwroot/app/app.js'],
  theme: {
    extend: {
      fontFamily: { display: ['Sora', 'Figtree', 'system-ui', 'sans-serif'], sans: ['Figtree', 'system-ui', '-apple-system', 'Segoe UI', 'Roboto', 'sans-serif'] },
      colors: {
        bg: tok('bg'), surface: tok('surface'), raised: tok('raised'), fg: tok('fg'), muted: tok('muted'), line: tok('line'),
        p50: tok('p50'), p100: tok('p100'), p200: tok('p200'), p300: tok('p300'), p500: tok('p500'), p600: tok('p600'), p700: tok('p700'), p800: tok('p800'), p900: tok('p900'),
        ok: tok('ok'), warn: tok('warn'), bad: tok('bad'), okbg: tok('okbg'), warnbg: tok('warnbg'), badbg: tok('badbg'),
        onp: tok('onp'), ink: tok('ink'), onink: tok('onink'), inkmuted: tok('inkmuted'), inkline: tok('inkline'), line2: tok('line2'), okf: tok('okf'), warnf: tok('warnf'), badf: tok('badf'), serf: tok('serf'), okline: tok('okline'), warnline: tok('warnline'), badline: tok('badline'), tint: tok('tint'), soft: tok('soft'), softfg: tok('softfg')
      }
    }
  }
};
