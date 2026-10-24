// Biscuit: a golden Shiba-like puppy with a red collar, body in profile, face turned three-quarters to the viewer (240 ×
// 240 units, its feet at y = 226). Idle, walk, run, jump, play (excited hops) and sleep.
(() => {
  const L = '#6A3B1C';
  const C = { fur: 'url(#biscuit-fur)', far: '#E2954A', shade: '#DB8538', cream: '#FFF3E1', creamShade: '#F1DCC0' };
  const defs = `
    <radialGradient id="biscuit-shadow"><stop offset="0" stop-color="#000" stop-opacity="0.38"/><stop offset="1" stop-color="#000" stop-opacity="0"/></radialGradient>
    <linearGradient id="biscuit-fur" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#F7B866"/><stop offset="1" stop-color="#EE9D49"/></linearGradient>
    <linearGradient id="biscuit-head" x1="0" y1="0" x2="0.25" y2="1"><stop offset="0" stop-color="#F9BE6E"/><stop offset="1" stop-color="#EE9C47"/></linearGradient>
    <radialGradient id="biscuit-iris" cx="0.5" cy="0.65" r="0.65"><stop offset="0" stop-color="#9B5A2E"/><stop offset="0.6" stop-color="#4A2512"/><stop offset="1" stop-color="#1E0D06"/></radialGradient>
    <linearGradient id="biscuit-nose" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#5A3A2C"/><stop offset="1" stop-color="#1E120D"/></linearGradient>
    <radialGradient id="biscuit-blush"><stop offset="0" stop-color="#FF8A8A" stop-opacity="0.5"/><stop offset="1" stop-color="#FF8A8A" stop-opacity="0"/></radialGradient>
    <linearGradient id="biscuit-ear" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#FFE3D2"/><stop offset="1" stop-color="#FFB9A8"/></linearGradient>
    <linearGradient id="biscuit-tag" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#FFF1A8"/><stop offset="1" stop-color="#E0A21C"/></linearGradient>
  `;
  const eye = (cx, cy, rx, ry) => `
    <ellipse cx="${cx}" cy="${cy}" rx="${rx}" ry="${ry}" fill="#1E0D06"/>
    <ellipse cx="${cx}" cy="${cy + 1}" rx="${rx - 1.4}" ry="${ry - 1.5}" fill="url(#biscuit-iris)"/>
    <ellipse cx="${cx + rx * 0.3}" cy="${cy - ry * 0.36}" rx="${rx * 0.38}" ry="${ry * 0.32}" fill="#FFFFFF"/>
    <circle cx="${cx - rx * 0.36}" cy="${cy + ry * 0.4}" r="${rx * 0.17}" fill="#FFFFFF" opacity="0.9"/>
    <circle cx="${cx + rx * 0.02}" cy="${cy - ry * 0.66}" r="${rx * 0.09}" fill="#FFFFFF" opacity="0.8"/>`;
  const leg = (id, d, fill, sock) => layered('biscuit-' + id, d, { fill, line: L, width: 2.2, extra: `<path d="M0 ${sock} H260 V240 H0 Z" fill="${C.cream}"/><path d="M0 ${sock} H260" stroke="${C.creamShade}" stroke-width="1.2"/>` });
  const body = 'M160 156 C 147 135, 112 131, 88 137 C 65 142, 53 157, 54 178 C 55 199, 73 209, 100 209 L 143 209 C 165 209, 177 195, 175 176 C 174 167, 168 160, 160 156 Z';
  const head = 'M134 110 C 132 84, 152 66, 177 66 C 203 66, 220 83, 221 106 C 222 118, 218 128, 212 136 C 205 150, 191 158, 175 158 C 157 158, 141 149, 136 133 C 134 126, 134 118, 134 110 Z';
  const curl = 'M70 158 C 52 152, 46 128, 62 117 C 76 108, 93 116, 89 131 C 87 140, 77 141, 76 134';
  CHARACTERS.biscuit = {
    id: 'biscuit', name: 'Biscuit', anchor: [118, 226], head: [176, 68], shadow: 66, defs,
    parts: [
      { id: 'torso', pivot: [112, 209] },
      { id: 'tail', parent: 'torso', pivot: [70, 158], svg: `
        <path d="${curl}" fill="none" stroke="${L}" stroke-width="19" stroke-linecap="round"/>
        <path d="${curl}" fill="none" stroke="#F3AA58" stroke-width="14.5" stroke-linecap="round"/>
        <path d="M58 124 C 66 114, 80 113, 86 122" fill="none" stroke="${C.cream}" stroke-width="5" stroke-linecap="round"/>
        <path d="M64 146 C 58 140, 56 132, 58 126" fill="none" stroke="#FFD49A" stroke-width="3" stroke-linecap="round" opacity="0.8"/>` },
      { id: 'legBF', parent: 'torso', pivot: [97, 188], svg: leg('legBF', 'M88 182 C 80 196, 84 210, 90 219 C 92 225, 101 226, 105 225 C 111 224, 111 218, 107 213 C 103 205, 105 194, 107 186 Z', C.far, 212) },
      { id: 'legFF', parent: 'torso', pivot: [148, 188], svg: leg('legFF', 'M140 184 C 140 199, 140 211, 142 219 C 142 225, 149 227, 155 227 C 161 227, 163 223, 161 218 C 159 206, 157 194, 155 183 Z', C.far, 214) },
      { id: 'legBN', parent: 'torso', pivot: [91, 186], svg: leg('legBN', 'M81 179 C 72 195, 76 210, 82 220 C 84 226, 94 227, 98 226 C 104 225, 104 219, 100 214 C 96 206, 98 194, 101 183 Z', C.fur, 213) },
      { id: 'legFN', parent: 'torso', pivot: [160, 188], svg: leg('legFN', 'M152 185 C 152 200, 152 212, 154 220 C 154 225, 161 227, 167 227 C 173 227, 175 223, 173 218 C 171 206, 169 194, 167 183 Z', C.fur, 215) },
      { id: 'body', parent: 'torso', svg: layered('biscuit-body', body, { fill: C.fur, line: L, width: 2.4,
        shade: { d: 'M40 186 C 90 199, 140 198, 190 182 L 190 230 L 40 230 Z', fill: C.cream },
        light: { d: 'M80 146 C 102 137, 132 137, 154 146 C 134 143, 104 146, 82 154 Z', fill: '#FFFFFF', o: 0.45 },
        extra: `<path d="M40 186 C 90 199, 140 198, 190 182" fill="none" stroke="${C.creamShade}" stroke-width="2"/><path d="M118 152 C 123 149, 128 148, 133 149 M108 160 C 112 157, 117 156, 122 157" fill="none" stroke="#FFD49A" stroke-width="1.8" stroke-linecap="round"/>` }) },
      { id: 'neck', parent: 'torso', svg: `<path d="M134 152 C 138 135, 162 127, 177 135 C 186 146, 185 163, 177 174 C 165 180, 147 178, 139 168 C 135 163, 133 157, 134 152 Z" fill="url(#biscuit-fur)"/><path d="M177 135 C 186 146, 185 163, 177 174" fill="none" stroke="#6A3B1C" stroke-width="2.4" stroke-linecap="round"/>` },
      { id: 'chest', parent: 'torso', svg: `<path d="M148 146 C 160 150, 172 157, 175 167 C 175 173, 171 178, 166 180 Q 163 185, 159 182 Q 155 186, 151 182 Q 147 185, 146 179 C 142 170, 143 157, 148 146 Z" fill="${C.cream}" stroke="#D9B488" stroke-width="1.3"/>
        <path d="M148 150 C 158 158, 172 162, 186 158" fill="none" stroke="#8E1F1F" stroke-width="8" stroke-linecap="round"/>
        <path d="M148 150 C 158 158, 172 162, 186 158" fill="none" stroke="#E64545" stroke-width="5.5" stroke-linecap="round"/>
        <path d="M152 152 C 160 157, 170 159, 180 158" fill="none" stroke="#FF8A8A" stroke-width="1.4" stroke-linecap="round"/>
        <circle cx="169" cy="168" r="6" fill="url(#biscuit-tag)" stroke="#8A5A10" stroke-width="1.3"/><circle cx="167" cy="166" r="1.8" fill="#FFFFFF" opacity="0.8"/>` },
      { id: 'head', parent: 'torso', pivot: [160, 150], offset: [-2, 5] },
      { id: 'earF', parent: 'head', pivot: [154, 78], svg: layered('biscuit-earF', 'M140 94 C 136 72, 141 56, 150 47 C 159 57, 167 70, 169 80 Z', { fill: '#EE9D49', line: L, extra: `<path d="M145 84 C 144 70, 147 62, 151 57 C 156 64, 160 70, 162 76 Z" fill="url(#biscuit-ear)"/>` }) },
      { id: 'earN', parent: 'head', pivot: [202, 76], svg: layered('biscuit-earN', 'M186 74 C 194 62, 205 51, 215 46 C 219 61, 219 78, 213 93 Z', { fill: C.fur, line: L, extra: `<path d="M194 72 C 200 64, 206 58, 211 55 C 213 64, 213 75, 210 83 Z" fill="url(#biscuit-ear)"/>` }) },
      { id: 'skull', parent: 'head', svg: layered('biscuit-head', head, { fill: 'url(#biscuit-head)', line: L, width: 2.6,
        shade: { d: 'M126 122 C 140 150, 160 162, 180 164 L 120 170 Z', fill: C.cream },
        light: { d: 'M150 78 C 164 69, 188 69, 202 78 C 188 76, 166 77, 152 86 Z', fill: '#FFFFFF', o: 0.55 },
        extra: `<path d="M206 116 C 214 124, 222 126, 226 120 L 226 170 L 196 170 Z" fill="${C.cream}"/>
                <ellipse cx="164" cy="93" rx="5.5" ry="3.6" fill="${C.cream}"/><ellipse cx="197" cy="89" rx="6" ry="3.8" fill="${C.cream}"/>` }) },
      { id: 'muzzle', parent: 'head', svg: `<path d="M172 132 C 172 120, 189 116, 201 120 C 213 116, 225 124, 223 136 C 221 148, 209 152, 198 150 C 185 154, 172 146, 172 132 Z" fill="${C.cream}"/>
        <ellipse cx="153" cy="128" rx="11" ry="6.5" fill="url(#biscuit-blush)"/><ellipse cx="216" cy="122" rx="8" ry="5.5" fill="url(#biscuit-blush)"/>
        <path d="M196 123 C 196 118, 212 118, 212 123 C 212 128, 206 131.5, 204 131.5 C 202 131.5, 196 128, 196 123 Z" fill="url(#biscuit-nose)"/>
        <ellipse cx="201" cy="121.5" rx="3" ry="1.6" fill="#FFFFFF" opacity="0.6"/>` },
      { id: 'mouth', parent: 'head', svg: `<path d="M193 138 C 196 151, 212 152, 215 138 C 209 142, 199 142, 193 138 Z" fill="#7A1F2A" stroke="${L}" stroke-width="1.6" stroke-linejoin="round"/>
        <path d="M198 144 C 198 156, 211 156, 211 144 C 207 147, 202 147, 198 144 Z" fill="#FF7E93"/><path d="M204.5 145.5 L 204.5 152" stroke="#E25A72" stroke-width="1.2" stroke-linecap="round"/>
        <path d="M204 131.5 C 204 137, 199 140, 193 138 M204 131.5 C 204 137, 209 140, 215 138" fill="none" stroke="${L}" stroke-width="1.8" stroke-linecap="round"/>` },
      { id: 'eyes', parent: 'head', pivot: [180, 106], blink: true, svg: eye(164, 108, 8.5, 10.5) + eye(196, 104, 9.5, 11.5) },
      { id: 'shut', parent: 'head', hidden: true, svg: `<path d="M155 109 Q 164 115 173 109 M186 105 Q 196 112 206 105" fill="none" stroke="${L}" stroke-width="2.4" stroke-linecap="round"/>` },
    ],
    anims: {
      idle: { duration: 2.4, tracks: {
        torso: [[0, {}], [0.5, { sy: 1.02 }], [1, {}]],
        tail: [[0, { r: -8 }], [0.125, { r: 10 }], [0.25, { r: -8 }], [0.375, { r: 10 }], [0.5, { r: -8 }], [0.625, { r: 10 }], [0.75, { r: -8 }], [0.875, { r: 10 }], [1, { r: -8 }]],
        head: [[0, {}], [0.35, { r: 4, y: -0.5 }], [0.7, { r: -2 }], [1, {}]],
        mouth: [[0, {}], [0.5, { sy: 1.08 }], [1, {}]],
        earN: [[0, {}], [0.5, { r: 4 }], [1, {}]],
      } },
      walk: { duration: 0.85, tracks: {
        legFN: [[0, { r: 20 }], [0.5, { r: -20 }], [1, { r: 20 }]], legBF: [[0, { r: 20 }], [0.5, { r: -20 }], [1, { r: 20 }]],
        legFF: [[0, { r: -20 }], [0.5, { r: 20 }], [1, { r: -20 }]], legBN: [[0, { r: -20 }], [0.5, { r: 20 }], [1, { r: -20 }]],
        torso: [[0, {}], [0.25, { y: -2.2 }], [0.5, {}], [0.75, { y: -2.2 }], [1, {}]],
        head: [[0, { r: 1 }], [0.25, { y: -1.6, r: -1.5 }], [0.5, { r: 1 }], [0.75, { y: -1.6, r: -1.5 }], [1, { r: 1 }]],
        tail: [[0, { r: -10 }], [0.25, { r: 10 }], [0.5, { r: -10 }], [0.75, { r: 10 }], [1, { r: -10 }]],
        earN: [[0, { r: -3 }], [0.5, { r: 4 }], [1, { r: -3 }]],
      } },
      jump: { duration: 1.05, tracks: {
        root: [[0, {}], [0.12, { sy: 0.87, sx: 1.05 }], [0.45, { y: -44, sy: 1.06, sx: 0.97 }], [0.78, {}], [0.86, { sy: 0.9, sx: 1.04 }], [1, {}]],
        legFN: [[0, {}], [0.3, { r: -36 }], [0.68, { r: -30 }], [0.8, {}], [1, {}]], legFF: [[0, {}], [0.3, { r: -30 }], [0.68, { r: -24 }], [0.8, {}], [1, {}]],
        legBN: [[0, {}], [0.3, { r: 36 }], [0.68, { r: 26 }], [0.8, {}], [1, {}]], legBF: [[0, {}], [0.3, { r: 30 }], [0.68, { r: 22 }], [0.8, {}], [1, {}]],
        tail: [[0, {}], [0.45, { r: 18 }], [0.85, { r: -10 }], [1, {}]],
        earN: [[0, {}], [0.45, { r: -12 }], [0.85, { r: 6 }], [1, {}]],
        head: [[0, {}], [0.45, { r: -6 }], [0.9, { r: 3 }], [1, {}]],
      } },
      run: { duration: 0.48, tracks: {
        root: [[0, {}], [0.25, { y: -7 }], [0.5, {}], [0.75, { y: -2.5 }], [1, {}]],
        torso: [[0, { r: -4, sx: 1.04 }], [0.5, { r: 4, sx: 0.97 }], [1, { r: -4, sx: 1.04 }]],
        legFN: [[0, { r: -38 }], [0.5, { r: 30 }], [1, { r: -38 }]], legFF: [[0, { r: -30 }], [0.5, { r: 36 }], [1, { r: -30 }]],
        legBN: [[0, { r: 34 }], [0.5, { r: -32 }], [1, { r: 34 }]], legBF: [[0, { r: 28 }], [0.5, { r: -36 }], [1, { r: 28 }]],
        head: [[0, { r: 3 }], [0.5, { r: -3, y: 1 }], [1, { r: 3 }]],
        tail: [[0, { r: -12 }], [0.25, { r: 10 }], [0.5, { r: -12 }], [0.75, { r: 10 }], [1, { r: -12 }]],
        earN: [[0, { r: -12 }], [1, { r: -12 }]], earF: [[0, { r: -8 }], [1, { r: -8 }]],
        mouth: [[0, { sy: 1.1 }], [1, { sy: 1.1 }]],
      } },
      play: { duration: 1.3, tracks: {
        root: [[0, {}], [0.08, { sy: 0.9, sx: 1.04 }], [0.25, { y: -18, sy: 1.04, sx: 0.98 }], [0.42, { sy: 0.92, sx: 1.03 }], [0.5, {}], [0.58, { sy: 0.9, sx: 1.04 }], [0.75, { y: -18, sy: 1.04, sx: 0.98 }], [0.92, { sy: 0.92, sx: 1.03 }], [1, {}]],
        legFN: [[0, {}], [0.25, { r: -20 }], [0.42, {}], [0.75, { r: -20 }], [0.92, {}], [1, {}]], legFF: [[0, {}], [0.25, { r: -16 }], [0.42, {}], [0.75, { r: -16 }], [0.92, {}], [1, {}]],
        legBN: [[0, {}], [0.25, { r: 16 }], [0.42, {}], [0.75, { r: 16 }], [0.92, {}], [1, {}]], legBF: [[0, {}], [0.25, { r: 12 }], [0.42, {}], [0.75, { r: 12 }], [0.92, {}], [1, {}]],
        tail: [[0, { r: -14 }], [0.1, { r: 14 }], [0.2, { r: -14 }], [0.3, { r: 14 }], [0.4, { r: -14 }], [0.5, { r: 14 }], [0.6, { r: -14 }], [0.7, { r: 14 }], [0.8, { r: -14 }], [0.9, { r: 14 }], [1, { r: -14 }]],
        head: [[0, { r: -4 }], [0.25, { r: 6 }], [0.5, { r: -6 }], [0.75, { r: 6 }], [1, { r: -4 }]],
        earN: [[0, {}], [0.25, { r: -14 }], [0.42, { r: 6 }], [0.5, {}], [0.75, { r: -14 }], [0.92, { r: 6 }], [1, {}]],
        earF: [[0, {}], [0.25, { r: -10 }], [0.42, { r: 4 }], [0.5, {}], [0.75, { r: -10 }], [0.92, { r: 4 }], [1, {}]],
        mouth: [[0, {}], [0.25, { sy: 1.15 }], [0.5, {}], [0.75, { sy: 1.15 }], [1, {}]],
      } },
      sleep: { duration: 4.0, tracks: {
        torso: [[0, { y: 14, sy: 0.82 }], [0.5, { y: 14, sy: 0.85 }], [1, { y: 14, sy: 0.82 }]],
        legFN: [[0, { o: 0 }], [1, { o: 0 }]], legFF: [[0, { o: 0 }], [1, { o: 0 }]], legBN: [[0, { o: 0 }], [1, { o: 0 }]], legBF: [[0, { o: 0 }], [1, { o: 0 }]],
        head: [[0, { r: 10, x: -8, y: 18 }], [0.5, { r: 11, x: -8, y: 18.6 }], [1, { r: 10, x: -8, y: 18 }]],
        eyes: [[0, { o: 0 }], [1, { o: 0 }]], shut: [[0, { o: 1 }], [1, { o: 1 }]], mouth: [[0, { o: 0 }], [1, { o: 0 }]],
      } },
    },
  };
})();
