// Mochi: a cream tabby kitten, body in profile, face turned three-quarters to the viewer (240 × 240 units, its feet at
// y = 226). Idle, walk, run, jump, play (a crouch, a wiggle and a pounce) and sleep.
(() => {
  const L = '#6B4430';
  const C = {
    fur: 'url(#mochi-fur)', furFar: '#EED3AE', shade: '#EFC896', tabby: '#F0A15A', tabbyDark: '#DD8A3E', white: '#FFF9F0', pink: '#FF9FB2',
  };
  const defs = `
    <radialGradient id="mochi-shadow"><stop offset="0" stop-color="#000" stop-opacity="0.38"/><stop offset="1" stop-color="#000" stop-opacity="0"/></radialGradient>
    <linearGradient id="mochi-fur" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#FFF3DF"/><stop offset="1" stop-color="#F9DDB5"/></linearGradient>
    <linearGradient id="mochi-head" x1="0" y1="0" x2="0.3" y2="1"><stop offset="0" stop-color="#FFF4E2"/><stop offset="1" stop-color="#F8DCB2"/></linearGradient>
    <radialGradient id="mochi-iris" cx="0.5" cy="0.62" r="0.62"><stop offset="0" stop-color="#FFF2A8"/><stop offset="0.45" stop-color="#A7DE6A"/><stop offset="1" stop-color="#2F7F45"/></radialGradient>
    <linearGradient id="mochi-nose" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#FFB8C6"/><stop offset="1" stop-color="#EE7189"/></linearGradient>
    <radialGradient id="mochi-blush"><stop offset="0" stop-color="#FF8FA6" stop-opacity="0.6"/><stop offset="1" stop-color="#FF8FA6" stop-opacity="0"/></radialGradient>
    <linearGradient id="mochi-ear" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#FFD3DC"/><stop offset="1" stop-color="#FF9CB0"/></linearGradient>
  `;
  const eye = (cx, cy, rx, ry, key) => `
    <ellipse cx="${cx}" cy="${cy}" rx="${rx}" ry="${ry}" fill="#2A1A14"/>
    <ellipse cx="${cx}" cy="${cy + 1.2}" rx="${rx - 1.6}" ry="${ry - 1.8}" fill="url(#mochi-iris)"/>
    <ellipse cx="${cx}" cy="${cy + 1.5}" rx="${rx * 0.42}" ry="${ry * 0.6}" fill="#1B1210"/>
    <ellipse cx="${cx + rx * 0.32}" cy="${cy - ry * 0.38}" rx="${rx * 0.36}" ry="${ry * 0.3}" fill="#FFFFFF"/>
    <circle cx="${cx - rx * 0.38}" cy="${cy + ry * 0.42}" r="${rx * 0.16}" fill="#FFFFFF" opacity="0.9"/>
    <circle cx="${cx + rx * 0.05}" cy="${cy - ry * 0.62}" r="${rx * 0.09}" fill="#FFFFFF" opacity="0.85"/>
    <path d="M${cx - rx + 1.5} ${cy + ry * 0.55} Q ${cx} ${cy + ry + 0.5} ${cx + rx - 1.5} ${cy + ry * 0.55}" fill="none" stroke="#FFFFFF" stroke-width="1.1" opacity="0.35"/>`;
  let legs = 0;
  const leg = (d, fill, toes) => layered(`mochi-leg${++legs}`, d, { fill, line: L, width: 2.2, shade: { d: 'M0 214 H260 V240 H0 Z', fill: '#E9C08C', o: 0.55 }, extra: '' }) + (toes ? `<path d="${toes}" fill="none" stroke="${L}" stroke-width="1.4" stroke-linecap="round"/>` : '');
  const body = 'M158 160 C 150 140, 120 135, 96 139 C 73 143, 59 156, 59 176 C 59 196, 76 207, 100 207 L 140 207 C 161 207, 173 195, 171 178 C 170 170, 165 164, 158 160 Z';
  const tail = 'M66 174 C 42 170, 27 150, 31 127 C 33 114, 43 107, 51 112 C 56 116, 50 125, 49 136 C 48 152, 57 161, 72 165 Z';
  const head = 'M128 108 C 128 81, 148 65, 172 65 C 198 65, 216 83, 216 108 C 216 120, 212 128, 207 134 L 213 141 L 202 141 C 194 149, 182 153, 170 153 C 158 153, 146 149, 138 141 L 129 142 L 134 134 C 130 128, 128 118, 128 108 Z';
  CHARACTERS.mochi = {
    id: 'mochi', name: 'Mochi', anchor: [120, 226], head: [169, 68], shadow: 62, defs,
    parts: [
      { id: 'torso', pivot: [112, 207] },
      { id: 'tail', parent: 'torso', pivot: [68, 170], svg: layered('mochi-tail', tail, { fill: C.fur, line: L, shade: { d: 'M20 100 C 40 140, 56 160, 80 170 L 80 200 L 0 200 Z', fill: C.shade, o: 0.5 },
        extra: `<path d="M36 120 C 42 122, 48 126, 50 130 L 47 135 C 44 130, 38 126, 31 126 Z M33 141 C 40 142, 46 145, 49 150 L 50 155 C 45 151, 39 149, 32 149 Z M40 158 C 46 158, 52 161, 56 165 L 58 168 C 52 166, 46 164, 40 164 Z" fill="${C.tabby}"/>` }) },
      { id: 'legBF', parent: 'torso', pivot: [101, 188], svg: leg('M93 182 C 85 196, 89 210, 95 219 C 97 225, 105 226, 109 225 C 115 224, 115 218, 111 213 C 107 205, 109 194, 111 186 Z', C.furFar) },
      { id: 'legFF', parent: 'torso', pivot: [147, 188], svg: leg('M140 186 C 140 200, 140 212, 142 220 C 142 225, 148 227, 154 227 C 160 227, 162 223, 160 218 C 158 206, 156 194, 154 184 Z', C.furFar) },
      { id: 'legBN', parent: 'torso', pivot: [94, 186], svg: leg('M85 180 C 76 195, 80 210, 86 220 C 88 226, 97 227, 101 226 C 107 225, 107 219, 103 214 C 99 206, 101 194, 104 184 Z', C.fur, 'M92 222 L 92 226 M97 222 L 97 226') },
      { id: 'legFN', parent: 'torso', pivot: [158, 188], svg: leg('M151 186 C 151 200, 151 212, 153 220 C 153 225, 159 227, 165 227 C 171 227, 173 223, 171 218 C 169 206, 167 194, 165 184 Z', C.fur, 'M159 222 L 159 226 M164 222 L 164 226') },
      { id: 'body', parent: 'torso', svg: layered('mochi-body', body, { fill: C.fur, line: L, width: 2.4,
        shade: { d: 'M40 188 C 90 199, 140 198, 190 184 L 190 230 L 40 230 Z', fill: C.shade, o: 0.75 },
        light: { d: 'M82 147 C 102 139, 130 139, 152 148 C 132 145, 103 148, 84 156 Z', fill: '#FFFFFF', o: 0.55 },
        extra: `<path d="M66 162 C 78 141, 118 134, 150 147 C 132 152, 104 156, 70 176 Z" fill="${C.tabby}"/>
                <path d="M96 140 C 101 150, 100 159, 94 166 C 99 157, 99 149, 92 141 Z M112 138 C 117 148, 116 158, 110 165 C 115 156, 115 147, 108 139 Z M80 146 C 86 155, 85 164, 79 171 C 84 162, 84 154, 76 148 Z" fill="${C.tabbyDark}"/>
                <path d="M118 196 C 130 202, 150 202, 164 192 C 160 204, 140 210, 120 206 Z" fill="${C.white}" opacity="0.85"/>
                <path d="M128 150 C 132 147, 137 146, 142 147 M120 158 C 124 155, 129 154, 133 155" fill="none" stroke="#FFFFFF" stroke-width="1.6" stroke-linecap="round" opacity="0.7"/>` }) },
      { id: 'neck', parent: 'torso', svg: `<path d="M134 152 C 138 136, 160 128, 174 136 C 182 146, 181 162, 174 172 C 163 178, 146 176, 139 167 C 135 162, 133 157, 134 152 Z" fill="url(#mochi-fur)"/><path d="M174 136 C 182 146, 181 162, 174 172" fill="none" stroke="#6B4430" stroke-width="2.4" stroke-linecap="round"/>` },
      { id: 'chest', parent: 'torso', svg: `<path d="M147 147 C 158 150, 168 156, 171 165 C 171 171, 168 175, 163 177 Q 161 182, 157 179 Q 153 183, 150 179 Q 146 182, 145 176 C 142 168, 143 156, 147 147 Z" fill="${C.white}" stroke="#D2B48E" stroke-width="1.3" stroke-linejoin="round"/><path d="M151 160 C 154 163, 158 164, 162 164" fill="none" stroke="#EADBC6" stroke-width="1.3" stroke-linecap="round"/>` },
      { id: 'head', parent: 'torso', pivot: [158, 150], offset: [-3, 7] },
      { id: 'earF', parent: 'head', pivot: [148, 76], svg: layered('mochi-earF', 'M134 90 C 130 70, 134 52, 141 45 C 150 53, 158 63, 163 73 Z', { fill: C.fur, line: L, extra: `<path d="M139 80 C 138 67, 140 58, 143 54 C 148 60, 152 66, 155 72 Z" fill="url(#mochi-ear)"/>` }) },
      { id: 'earN', parent: 'head', pivot: [196, 74], svg: layered('mochi-earN', 'M181 71 C 187 60, 198 50, 209 45 C 213 58, 213 74, 209 89 Z', { fill: C.fur, line: L, extra: `<path d="M189 69 C 194 61, 200 56, 205 53 C 207 62, 207 72, 205 80 Z" fill="url(#mochi-ear)"/><path d="M196 66 L 200 60 M200 70 L 204 64" stroke="${C.white}" stroke-width="1.6" stroke-linecap="round"/>` }) },
      { id: 'skull', parent: 'head', svg: layered('mochi-head', head, { fill: 'url(#mochi-head)', line: L, width: 2.6,
        shade: { d: 'M120 126 C 150 150, 190 150, 222 124 L 222 160 L 120 160 Z', fill: C.shade, o: 0.55 },
        light: { d: 'M146 80 C 158 70, 182 69, 196 78 C 182 76, 162 77, 148 86 Z', fill: '#FFFFFF', o: 0.7 },
        extra: `<path d="M162 70 C 165 77, 165 84, 163 89 M173 67 C 174 75, 174 83, 172 90 M184 69 C 183 76, 182 83, 182 88" stroke="${C.tabby}" stroke-width="4" stroke-linecap="round" fill="none"/>
                <path d="M131 112 C 136 112, 141 114, 144 117 M131 120 C 136 119, 140 120, 143 122" stroke="${C.tabby}" stroke-width="3" stroke-linecap="round" fill="none"/>` }) },
      { id: 'muzzle', parent: 'head', svg: `<path d="M163 129 C 163 120, 172 118, 180 122 C 188 118, 198 120, 198 129 C 198 137, 190 141, 181 139 C 172 141, 163 137, 163 129 Z" fill="${C.white}"/>
        <ellipse cx="150" cy="128" rx="12" ry="7" fill="url(#mochi-blush)"/><ellipse cx="206" cy="126" rx="10" ry="6.5" fill="url(#mochi-blush)"/>
        <path d="M175 121.5 C 175 118.5, 187 118.5, 187 121.5 C 187 124.5, 183 127, 181 127 C 179 127, 175 124.5, 175 121.5 Z" fill="url(#mochi-nose)" stroke="${L}" stroke-width="1.1"/>
        <path d="M181 127 C 181 131.5, 178 133.5, 174.5 132.5 M181 127 C 181 131.5, 184 133.5, 187.5 132.5" fill="none" stroke="${L}" stroke-width="1.7" stroke-linecap="round"/>
        <path d="M162 131 L 140 127 M162 135 L 141 137 M199 129 L 220 124 M199 133 L 221 134" stroke="#B48A6A" stroke-width="1" stroke-linecap="round" opacity="0.8"/>` },
      { id: 'eyes', parent: 'head', pivot: [176, 108], blink: true, svg: eye(157, 109, 9.5, 12.5) + eye(195, 107, 11, 13.5) },
      { id: 'shut', parent: 'head', hidden: true, svg: `<path d="M147 110 Q 157 117 167 110 M184 108 Q 195 116 206 108" fill="none" stroke="${L}" stroke-width="2.4" stroke-linecap="round"/>` },
    ],
    anims: {
      idle: { duration: 3.2, tracks: {
        torso: [[0, {}], [0.5, { sy: 1.018 }], [1, {}]],
        tail: [[0, { r: -4 }], [0.35, { r: 9 }], [0.7, { r: -2 }], [1, { r: -4 }]],
        head: [[0, {}], [0.3, { r: -3, y: -0.6 }], [0.7, { r: 2 }], [1, {}]],
        earN: [[0, {}], [0.62, {}], [0.66, { r: -14 }], [0.72, {}], [1, {}]],
      } },
      walk: { duration: 0.95, tracks: {
        legFN: [[0, { r: 18 }], [0.5, { r: -18 }], [1, { r: 18 }]], legBF: [[0, { r: 18 }], [0.5, { r: -18 }], [1, { r: 18 }]],
        legFF: [[0, { r: -18 }], [0.5, { r: 18 }], [1, { r: -18 }]], legBN: [[0, { r: -18 }], [0.5, { r: 18 }], [1, { r: -18 }]],
        torso: [[0, {}], [0.25, { y: -2 }], [0.5, {}], [0.75, { y: -2 }], [1, {}]],
        head: [[0, { r: 1 }], [0.25, { y: -1.4, r: -1 }], [0.5, { r: 1 }], [0.75, { y: -1.4, r: -1 }], [1, { r: 1 }]],
        tail: [[0, { r: 6 }], [0.5, { r: -6 }], [1, { r: 6 }]],
      } },
      jump: { duration: 1.1, tracks: {
        root: [[0, {}], [0.12, { sy: 0.88, sx: 1.05 }], [0.45, { y: -46, sy: 1.06, sx: 0.97 }], [0.78, {}], [0.86, { sy: 0.9, sx: 1.04 }], [1, {}]],
        legFN: [[0, {}], [0.3, { r: -34 }], [0.68, { r: -30 }], [0.8, {}], [1, {}]], legFF: [[0, {}], [0.3, { r: -28 }], [0.68, { r: -24 }], [0.8, {}], [1, {}]],
        legBN: [[0, {}], [0.3, { r: 34 }], [0.68, { r: 26 }], [0.8, {}], [1, {}]], legBF: [[0, {}], [0.3, { r: 28 }], [0.68, { r: 22 }], [0.8, {}], [1, {}]],
        tail: [[0, {}], [0.45, { r: -24 }], [0.85, { r: 10 }], [1, {}]],
        head: [[0, {}], [0.45, { r: -6 }], [0.9, { r: 3 }], [1, {}]],
      } },
      run: { duration: 0.5, tracks: {
        root: [[0, {}], [0.25, { y: -7 }], [0.5, {}], [0.75, { y: -2.5 }], [1, {}]],
        torso: [[0, { r: -4, sx: 1.04 }], [0.5, { r: 4, sx: 0.97 }], [1, { r: -4, sx: 1.04 }]],
        legFN: [[0, { r: -38 }], [0.5, { r: 30 }], [1, { r: -38 }]], legFF: [[0, { r: -30 }], [0.5, { r: 36 }], [1, { r: -30 }]],
        legBN: [[0, { r: 34 }], [0.5, { r: -32 }], [1, { r: 34 }]], legBF: [[0, { r: 28 }], [0.5, { r: -36 }], [1, { r: 28 }]],
        head: [[0, { r: 3 }], [0.5, { r: -3, y: 1 }], [1, { r: 3 }]],
        tail: [[0, { r: -30 }], [0.5, { r: -20 }], [1, { r: -30 }]],
        earN: [[0, { r: -12 }], [1, { r: -12 }]], earF: [[0, { r: -8 }], [1, { r: -8 }]],
      } },
      play: { duration: 2.2, tracks: {
        root: [[0, {}], [0.08, { sy: 0.92, sx: 1.02 }], [0.52, { sy: 0.92, sx: 1.02 }], [0.6, { sy: 0.86, sx: 1.05 }], [0.71, { y: -40, sy: 1.06, sx: 0.96 }], [0.82, { sy: 0.9, sx: 1.05 }], [0.9, {}], [1, {}]],
        torso: [[0, {}], [0.1, { r: 2 }], [0.16, { r: -2 }], [0.22, { r: 2 }], [0.28, { r: -2 }], [0.34, { r: 2 }], [0.4, { r: -2 }], [0.46, { r: 2 }], [0.52, {}], [1, {}]],
        head: [[0, {}], [0.08, { r: 6, y: 3 }], [0.52, { r: 6, y: 3 }], [0.71, { r: -8 }], [0.86, { r: 2 }], [1, {}]],
        legFN: [[0, {}], [0.08, { r: -12 }], [0.56, { r: -12 }], [0.71, { r: -40 }], [0.82, { r: -8 }], [0.9, {}], [1, {}]],
        legFF: [[0, {}], [0.08, { r: -8 }], [0.56, { r: -8 }], [0.71, { r: -34 }], [0.82, { r: -5 }], [0.9, {}], [1, {}]],
        legBN: [[0, {}], [0.6, { r: 8 }], [0.71, { r: 36 }], [0.83, { r: 4 }], [1, {}]],
        legBF: [[0, {}], [0.6, { r: 6 }], [0.71, { r: 30 }], [0.83, { r: 3 }], [1, {}]],
        tail: [[0, { r: -4 }], [0.12, { r: 10 }], [0.22, { r: -6 }], [0.32, { r: 10 }], [0.42, { r: -6 }], [0.52, { r: 8 }], [0.71, { r: -26 }], [0.86, { r: 6 }], [1, { r: -4 }]],
        earN: [[0, {}], [0.08, { r: 8 }], [0.56, { r: 8 }], [0.71, { r: -10 }], [0.9, {}], [1, {}]],
        earF: [[0, {}], [0.08, { r: 6 }], [0.56, { r: 6 }], [0.71, { r: -8 }], [0.9, {}], [1, {}]],
      } },
      sleep: { duration: 4.0, tracks: {
        torso: [[0, { y: 14, sy: 0.82 }], [0.5, { y: 14, sy: 0.85 }], [1, { y: 14, sy: 0.82 }]],
        legFN: [[0, { o: 0 }], [1, { o: 0 }]], legFF: [[0, { o: 0 }], [1, { o: 0 }]], legBN: [[0, { o: 0 }], [1, { o: 0 }]], legBF: [[0, { o: 0 }], [1, { o: 0 }]],
        head: [[0, { r: 10, x: -8, y: 18 }], [0.5, { r: 11, x: -8, y: 18.6 }], [1, { r: 10, x: -8, y: 18 }]],
        eyes: [[0, { o: 0 }], [1, { o: 0 }]], shut: [[0, { o: 1 }], [1, { o: 1 }]],
        tail: [[0, { r: -95 }], [1, { r: -95 }]],
      } },
    },
  };
})();
