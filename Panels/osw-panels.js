/* ==========================================================================
   osw-panels.js  —  OSUPhoenix Streamworks shared panel helper
   Version 1.6.1   (pairs with osw-panels.css 1.6.x)
     1.6.2  (CSS) alert color wins over an inline accent
     1.6.1  reset(), apply(panel, cfg, { fresh: true })
     1.6.0  OSWPanels.burst(): confetti, coins, gems, images, shockwave,
            impact; engine gains custom draw, one-shot systems, launch/fall
     1.5.0  particle engine (shared Ticker, registerParticles) + "embers"
     1.4.0  "shine" effect; OSWPanels.play(); effects can react to show()
     1.3.0  "trace" effect (CSS-only); effects can skip the canvas
     1.2.0  6 shapes, 13 skins, 15 presets, iconShape, iconPosition,
            accent2, "electric" effect
     1.1.0  pattern textures: PATTERNS catalog, textureColors,
            texturePalette, textureOpacity
     1.0.0  first release
   --------------------------------------------------------------------------
   osw-panels.css does the drawing. This file is the "DLL" side: shared
   JavaScript every widget used to copy-paste for itself.

     OSWPanels.apply(panel, config)   set shape/skin/texture/colors/effect
     OSWPanels.show(panel) / hide()   play the enter/exit motion
     OSWPanels.play(panel)            fire the panel's effect once (shine
                                      sweeps, particle effects flare)
     OSWPanels.registerParticles()    add your own particle preset (v1.5)
     OSWPanels.burst(panel, type, o)  one-shot alert effects (v1.6)
     OSWPanels.swap(panel, fn)        fade content out, change it, fade in
     OSWPanels.fitText(el)            shrink text until it fits (Info Panel)
     OSWPanels.assets.*               platform logos, line icons, brand colors,
                                      SB-Social-Packs icon URLs
     OSWPanels.util.*                 colors, money formatting, Google Fonts
     OSWPanels.from*()                adapters that turn your EXISTING
                                      Streamer.bot settings into a config,
                                      so the C# side doesn't have to change

   ONE CONFIG SHAPE FOR EVERY WIDGET
   The object you pass to apply() is the same everywhere, so Streamer.bot
   can send it in any payload and any widget understands it:

     {
       preset:  "neonGlow",        optional starting point (see PRESETS)
       shape:   "hexagon",         SHAPES list below
       skin:    "neon",            SKINS list below
       texture: "circuit",         TEXTURES list below, or "none"
       texturePalette: "accent",   pattern textures: original | accent | mono
       textureColors: ["#f60","#111"],  pattern textures: your own colors
       textureOpacity: 0.4,        strength of any texture, 0-1
       motion:  "wipe",            MOTIONS list below
       effect:  "hexglow",         EFFECTS registry, or "none"
       effectOptions: { hexRadius: 22 },
       iconShape: "diamond",       ICON_SHAPES list below (v1.2)
       iconPosition: "bottom",     inline | top | bottom (v1.2)
       accent: "#ff6a00", accent2: "#00c8ff",   accent2 = second color (v1.2)
       alert: "#ffb300", fill: "rgba(0,0,0,.8)",
       text: "#fff", textDim: "rgba(255,255,255,.7)",
       font: "Rajdhani", fontIsGoogle: true, weight: 700,
       textureColor: "rgba(255,255,255,.08)", textureScale: 1.2,
       frameWidth: 3, radius: 14, chamfer: 26, notch: 16,   (numbers = px)
       strokeWidth: 1, strokeColor: "#000", textShadow: "0 2px 4px #000",
       inMs: 650, outMs: 450                                (numbers = ms)
     }

   Names are forgiving: "ChamferLeft", "chamferLeft" and "chamfer-left" all
   work, so C# enum names can be sent as-is.

   SETTING A CONFIG WITHOUT WRITING JS
     1. Classes in the markup         class="osw-panel osw-shape--pill"
     2. data- attributes              data-shape="pill" data-accent="#f60"
     3. The OBS browser source URL    index.html?skin=neon&accent=ff6a00
   Later ones win. Add data-osw-url="off" to a panel to ignore the URL.
   ========================================================================== */

(function (global) {
  'use strict';

  const VERSION = '1.6.2';

  /* ------------------------------------------------------------------
     CATALOG — what exists. The gallery page builds itself from these,
     so adding a design means: CSS rule + one line here.
     ------------------------------------------------------------------ */
  const SHAPES = ['chamfer-right', 'chamfer-left', 'hexagon', 'parallelogram', 'breadcrumb', 'ribbon',
    'pixelated', 'scooped', 'pill', 'rounded-rect', 'square', 'tab-top', 'tab-bottom', 'tooltip', 'cloud',
    // v1.2
    'top-tab', 'octagon', 'cut-corners', 'trapezoid', 'trapezoid-inverted', 'dart'];
  const SKINS = ['solid', 'edge', 'tag', 'chip', 'neon', 'glass', 'rgb', 'deco', 'pixel', 'fancy', 'bevel',
    // v1.2
    'hud', 'echo', 'hatch', 'slash', 'lightbar', 'shards', 'wings', 'speedlines', 'neon-lines',
    'gate', 'badge', 'chevron', 'header'];
  // v1.2: set on the panel, style its .osw-icon. "circle"/"inline" = default (no class).
  const ICON_SHAPES = ['circle', 'diamond', 'hexagon', 'pentagon', 'shield', 'square', 'rounded', 'slant'];
  const ICON_POSITIONS = ['inline', 'top', 'bottom'];
  /* One-color MASK textures: color comes from textureColor. */
  const MASK_TEXTURES = ['none', 'hex-grid', 'dots', 'stripes', 'scanlines', 'circuit', 'noise', 'diamond',
    'ripple', 'circles', 'honeycomb-filled', 'isometric', 'honeycomb', 'wavy'];

  /* Multi-color PATTERN textures (1.1.0): name -> original colors, one per
     color slot. The slot count is how many entries textureColors can have.
     Must match section 6b of osw-panels.css (both are generated from the
     same list, so keep them in step when adding one by hand). */
  const PATTERNS = {
    'hearts': ['#e7525b', '#78dbf0'],
    'stars': ['#fff220', '#e181c2'],
    'dungeon-squares': ['#7f727b', '#d6b4c2', '#baa0ab'],
    'wavy-squiggles': ['#3b2d38', '#f02475'],
    'diagonal-arcs': ['#dfba69', '#5a2e2e'],
    'bricks': ['#774f38', '#f1d4af'],
    'two-tone-bricks': ['#c0b299', '#a4a9aa', '#2c2e2a'],
    'pills': ['#ecbe13', '#309292'],
    'nested-rhombus': ['#542437', '#c02942'],
    'lollipop': ['#3fb8af', '#ff9e9d'],
    '3d-triangles': ['#cd2942', '#62928c', '#e8cba9', '#33152e', '#414352'],
    'curvy': ['#5e8c6a', '#bfb35a'],
    'cube-columns': ['#b2b2b2', '#ffffff', '#d9d9d9'],
    'mosaic-triangles': ['#b62f31', '#ecdacb', '#8e1f08'],
    'overlapping-cubes': ['#f2f2f2', '#cdcbcc', '#999999'],
    'distorted': ['#170409', '#67917a'],
    'diamond-stripes': ['#eb6841', '#edc951'],
    'chevrons': ['#e0e4cc', '#69d2e7'],
    'meander': ['#72e21f', '#044012']
  };
  const TEXTURES = MASK_TEXTURES.concat(Object.keys(PATTERNS));
  const PALETTES = ['original', 'accent', 'mono'];
  const MOTIONS = ['fade', 'rise', 'pop', 'slide', 'wipe'];

  /* Presets = named starting points. The first ten are the Donor Ticker's
     stylePreset names, so an existing ticker config maps straight across. */
  const PRESETS = {
    osuphoenix: { shape: 'chamfer-right', skin: 'solid', texture: 'hex-grid' },
    pill:       { shape: 'pill',         skin: 'solid', texture: 'none' },
    barEdge:    { shape: 'rounded-rect', skin: 'edge',  texture: 'none' },
    ribbonTag:  { shape: 'rounded-rect', skin: 'tag',   texture: 'none' },
    chipCard:   { shape: 'rounded-rect', skin: 'chip',  texture: 'none' },
    neonGlow:   { shape: 'pill',         skin: 'neon',  texture: 'none' },
    glassPanel: { shape: 'rounded-rect', skin: 'glass', texture: 'none' },
    rgbGlow:    { shape: 'pill',         skin: 'rgb',   texture: 'none' },
    decoCard:   { shape: 'rounded-rect', skin: 'deco',  texture: 'none' },
    pixel8:     { shape: 'pixelated',    skin: 'pixel', texture: 'scanlines' },
    fancyFrame: { shape: 'rounded-rect', skin: 'fancy', texture: 'none' },
    hexGlowBar: { shape: 'square',       skin: 'bevel', texture: 'none', effect: 'hexglow' },

    // v1.2 — broadcast/esports alert styles. Color-free on purpose: they
    // take whatever accent you set.
    titleTab:    { shape: 'top-tab',            skin: 'hud',        texture: 'none' },
    doubleHex:   { shape: 'hexagon',            skin: 'echo',       texture: 'none' },
    hostBar:     { shape: 'octagon',            skin: 'header',     texture: 'none' },
    shardBurst:  { shape: 'cut-corners',        skin: 'shards',     texture: 'none' },
    arrowhead:   { shape: 'dart',               skin: 'chevron',    texture: 'none' },
    torii:       { shape: 'square',             skin: 'gate',       texture: 'none' },
    slab:        { shape: 'parallelogram',      skin: 'badge',      texture: 'none', iconShape: 'slant' },
    neonTube:    { shape: 'trapezoid',          skin: 'neon-lines', texture: 'none' },
    voltage:     { shape: 'cut-corners',        skin: 'solid',      texture: 'none', effect: 'electric', iconShape: 'pentagon' },
    command:     { shape: 'trapezoid',          skin: 'hud',        texture: 'hex-grid', iconShape: 'diamond' },
    circuitry:   { shape: 'octagon',            skin: 'wings',      texture: 'circuit' },
    vanguard:    { shape: 'parallelogram',      skin: 'speedlines', texture: 'none', iconShape: 'diamond' },
    slashline:   { shape: 'parallelogram',      skin: 'slash',      texture: 'none' },
    redline:     { shape: 'trapezoid-inverted', skin: 'hatch',      texture: 'none', iconShape: 'square' },
    lightbar:    { shape: 'rounded-rect',       skin: 'lightbar',   texture: 'none', iconShape: 'rounded' }
  };

  /* Old or alternate names -> current names. */
  const ALIASES = {
    shape:   { chamfer: 'chamfer-right', rounded: 'rounded-rect', round: 'rounded-rect', rect: 'square' },
    skin:    { 'bar-edge': 'edge', 'ribbon-tag': 'tag', 'chip-card': 'chip', 'neon-glow': 'neon',
               'glass-panel': 'glass', 'rgb-glow': 'rgb', 'deco-card': 'deco', pixel8: 'pixel',
               'fancy-frame': 'fancy', default: 'solid' },
    texture: { hexgrid: 'hex-grid', off: 'none', '': 'none',
               // C# enum spellings that kebab() can't turn into the right name
               '3-dtriangles': '3d-triangles', 'triangles3-d': '3d-triangles', 'three-d-triangles': '3d-triangles',
               'pill-pattern': 'pills', 'dungeon': 'dungeon-squares', 'squiggles': 'wavy-squiggles',
               'mosaic': 'mosaic-triangles', 'greek-key': 'meander' },
    iconShape: { round: 'circle', default: 'circle', '': 'circle', diamond: 'diamond', hex: 'hexagon', badge: 'pentagon' },
    iconPosition: { default: 'inline', '': 'inline', left: 'inline', above: 'top', below: 'bottom' },
    palette: { default: 'original', none: 'original', '': 'original', grey: 'mono', gray: 'mono', brand: 'accent' },
    motion:  {}
  };

  /* Config key -> CSS variable, and the unit a bare number gets. */
  const VARS = {
    accent: ['--osw-accent'], accent2: ['--osw-accent-2'], alert: ['--osw-alert'], fill: ['--osw-fill'],
    text: ['--osw-text'], textDim: ['--osw-text-dim'], frameColor: ['--osw-frame-paint'],
    weight: ['--osw-weight'],
    frameWidth: ['--osw-frame-w', 'px'], radius: ['--osw-radius', 'px'], chamfer: ['--osw-chamfer', 'px'],
    notch: ['--osw-notch', 'px'], step: ['--osw-step', 'px'], scoop: ['--osw-scoop', 'px'],
    cut: ['--osw-cut', 'px'], tabHeight: ['--osw-tab-h', 'px'], tabWidth: ['--osw-tab-w', 'px'],
    echoGap: ['--osw-echo-gap', 'px'], echoWidth: ['--osw-echo-w', 'px'], wingWidth: ['--osw-wing-w', 'px'],
    padX: ['--osw-pad-x', 'px'], padY: ['--osw-pad-y', 'px'], iconSize: ['--osw-icon-size', 'px'],
    textureColor: ['--osw-texture-color'], textureScale: ['--osw-texture-scale'],
    textureOpacity: ['--osw-texture-opacity'],
    strokeWidth: ['--osw-stroke-w', 'px'], strokeColor: ['--osw-stroke-color'], textShadow: ['--osw-text-shadow'],
    inMs: ['--osw-in-ms', 'ms'], outMs: ['--osw-out-ms', 'ms'], swapMs: ['--osw-swap-ms', 'ms']
  };

  // Every key apply() understands (used when reading data- attributes / URL).
  const KEYS = ['preset', 'shape', 'skin', 'texture', 'texturePalette', 'textureColors', 'iconShape', 'iconPosition',
    'motion', 'effect', 'font', 'fontIsGoogle'].concat(Object.keys(VARS));

  /* ------------------------------------------------------------------
     NAME HANDLING
     ------------------------------------------------------------------ */

  // "ChamferLeft" / "chamferLeft" / "chamfer_left" -> "chamfer-left"
  function kebab(s) {
    return String(s == null ? '' : s).trim()
      .replace(/([a-z0-9])([A-Z])/g, '$1-$2')
      .replace(/[\s_]+/g, '-')
      .toLowerCase();
  }
  function normalize(axis, value) {
    const k = kebab(value);
    return (ALIASES[axis] && ALIASES[axis][k] !== undefined) ? ALIASES[axis][k] : k;
  }
  function findPreset(name) {
    if (!name) return null;
    if (PRESETS[name]) return PRESETS[name];
    const want = kebab(name);                       // case-insensitive lookup
    const key = Object.keys(PRESETS).find(p => kebab(p) === want);
    return key ? PRESETS[key] : null;
  }

  /* ------------------------------------------------------------------
     STRUCTURE — add the layer divs if the widget didn't write them
     ------------------------------------------------------------------ */
  function upgrade(panel) {
    if (!panel.classList.contains('osw-panel')) panel.classList.add('osw-panel');

    let content = panel.querySelector(':scope > .osw-panel__content');
    if (!content) {
      // Move whatever the widget put inside the panel into a content div.
      content = document.createElement('div');
      content.className = 'osw-panel__content';
      while (panel.firstChild) content.appendChild(panel.firstChild);
      panel.appendChild(content);
    }
    const ensure = (cls, parent, before) => {
      let el = parent.querySelector(':scope > .' + cls);
      if (!el) {
        el = document.createElement('div');
        el.className = cls;
        parent.insertBefore(el, before);
      }
      return el;
    };
    ensure('osw-panel__frame', panel, content);
    const fill = ensure('osw-panel__fill', panel, content);
    ensure('osw-panel__texture', fill, fill.firstChild);
    ensure('osw-panel__deco', panel, content);
    return panel;
  }

  /* ------------------------------------------------------------------
     APPLY — the main entry point
     ------------------------------------------------------------------ */
  function setAxisClass(panel, axis, prefix, value, list) {
    if (value === undefined || value === null) return;
    const name = normalize(axis, value);
    // Remove any other class from the same axis, then add the new one.
    [...panel.classList].forEach(c => { if (c.startsWith(prefix)) panel.classList.remove(c); });
    if (name && list.includes(name)) panel.classList.add(prefix + name);
    else if (name) console.warn(`[OSWPanels] Unknown ${axis} "${value}". Known: ${list.join(', ')}`);
  }

  /* reset(): back to a plain panel. Removes every look class
     (shape/skin/texture/palette/motion/icon), every --osw-* override set
     inline, and the running effect. Content and widget classes stay. */
  const LOOK_PREFIXES = ['osw-shape--', 'osw-skin--', 'osw-tex--', 'osw-texpal--', 'osw-motion--',
    'osw-icon-shape--', 'osw-icon-pos--', 'osw-fx--'];
  function reset(panel) {
    if (!panel) return;
    const state = panel._osw;
    if (state && state.effect) state.effect.destroy();
    const fx = panel.querySelector(':scope > .osw-panel__fill > .osw-panel__fx');
    if (fx) fx.remove();
    [...panel.classList].forEach(c => { if (LOOK_PREFIXES.some(p => c.startsWith(p))) panel.classList.remove(c); });
    [...panel.style].forEach(prop => { if (prop.startsWith('--osw-')) panel.style.removeProperty(prop); });
    panel._osw = { cfg: {}, effect: null, effectName: 'none' };
  }

  /* apply(panel, config, { fresh: true }): with fresh, the panel is reset
     first, so the result is EXACTLY this config, with nothing left over
     from an earlier one. Use it when a whole saved style replaces another.
     Without it, apply() merges, which is right for small live tweaks
     ({ accent: "#f60" }). */
  function apply(panel, config, how) {
    if (!panel) return;
    if (how && how.fresh) reset(panel);
    upgrade(panel);
    const state = panel._osw || (panel._osw = { cfg: {}, effect: null, effectName: 'none' });

    // A preset fills in defaults; keys given directly still win.
    const preset = findPreset(config && config.preset);
    const cfg = Object.assign({}, preset || {}, config || {});
    Object.assign(state.cfg, cfg);

    setAxisClass(panel, 'shape', 'osw-shape--', cfg.shape, SHAPES);
    setAxisClass(panel, 'skin', 'osw-skin--', cfg.skin, SKINS);
    setAxisClass(panel, 'texture', 'osw-tex--', cfg.texture, TEXTURES);
    setAxisClass(panel, 'motion', 'osw-motion--', cfg.motion, MOTIONS);

    // Icon shape / position (v1.2). The default of each axis means "no class".
    if (cfg.iconShape !== undefined) {
      const v = normalize('iconShape', cfg.iconShape);
      setAxisClass(panel, 'iconShape', 'osw-icon-shape--', v === 'circle' ? null : v, ICON_SHAPES);
      if (v === 'circle') [...panel.classList].forEach(c => c.startsWith('osw-icon-shape--') && panel.classList.remove(c));
    }
    if (cfg.iconPosition !== undefined) {
      const v = normalize('iconPosition', cfg.iconPosition);
      setAxisClass(panel, 'iconPosition', 'osw-icon-pos--', v === 'inline' ? null : v, ICON_POSITIONS);
      if (v === 'inline') [...panel.classList].forEach(c => c.startsWith('osw-icon-pos--') && panel.classList.remove(c));
    }

    // Pattern colors. "original" = no class, so the pattern's own colors show.
    if (cfg.texturePalette !== undefined) {
      const pal = normalize('palette', cfg.texturePalette);
      [...panel.classList].forEach(c => { if (c.startsWith('osw-texpal--')) panel.classList.remove(c); });
      if (pal !== 'original') setAxisClass(panel, 'palette', 'osw-texpal--', pal, PALETTES);
    }
    // textureColors: ["#f00", "#111"] or "#f00,#111". Slot 1 = first color.
    // An empty entry ("" or null) leaves that slot on the palette/original.
    if (cfg.textureColors !== undefined) {
      const list = Array.isArray(cfg.textureColors) ? cfg.textureColors
        : String(cfg.textureColors || '').split(',').map(s => s.trim());
      for (let i = 1; i <= 5; i++) {
        const c = list[i - 1];
        if (c) panel.style.setProperty('--osw-pat-' + i, /^[0-9a-f]{3,8}$/i.test(c) ? '#' + c : c);
        else panel.style.removeProperty('--osw-pat-' + i);
      }
    }

    // Tokens. null or "" removes the override so the CSS default returns.
    for (const [key, [cssVar, unit]] of Object.entries(VARS)) {
      if (!(key in cfg)) continue;
      const v = cfg[key];
      if (v === null || v === '') panel.style.removeProperty(cssVar);
      else panel.style.setProperty(cssVar, (typeof v === 'number' && unit) ? v + unit : String(v));
    }

    // Fonts: quote the family and keep a fallback stack.
    if (cfg.font) {
      panel.style.setProperty('--osw-font', `"${String(cfg.font).replace(/"/g, '')}", "Segoe UI", system-ui, sans-serif`);
      if (cfg.fontIsGoogle) util.loadGoogleFont(cfg.font);
    }
    if (panel.classList.contains('osw-skin--pixel')) util.loadGoogleFont('Press Start 2P');

    // Effects: start, swap, or update.
    if ('effect' in cfg) {
      const name = kebab(cfg.effect || 'none');
      if (name !== state.effectName) {
        if (state.effect) state.effect.destroy();
        state.effect = null;
        state.effectName = name;
        // Always start from a clean slate: drop the old effect's canvas.
        const oldCanvas = panel.querySelector('.osw-panel__fx');
        if (oldCanvas) oldCanvas.remove();
        if (name !== 'none') {
          const factory = EFFECTS[name];
          if (!factory) console.warn(`[OSWPanels] Unknown effect "${cfg.effect}". Known: ${Object.keys(EFFECTS).join(', ')}`);
          else {
            // CSS-only effects (meta.canvas === false) get no canvas at all.
            const canvas = EFFECT_META[name].canvas === false ? null : fxCanvas(panel);
            // Only this call's options: a previous effect's options don't carry over.
            state.effect = factory(canvas, panel, cfg.effectOptions || {});
          }
        }
        return panel;
      }
    }
    if (state.effect) {
      if (cfg.effectOptions) state.effect.set(cfg.effectOptions);
      else if ('accent' in cfg) state.effect.refresh();   // follow the new accent color
    }
    return panel;
  }

  function fxCanvas(panel) {
    const fill = panel.querySelector(':scope > .osw-panel__fill');
    let c = fill.querySelector(':scope > .osw-panel__fx');
    if (!c) {
      c = document.createElement('canvas');
      c.className = 'osw-panel__fx';
      fill.appendChild(c);
    }
    return c;
  }

  /* ------------------------------------------------------------------
     READING CONFIG FROM MARKUP AND URL
     ------------------------------------------------------------------ */

  // Turn a string into the right type: "12" -> 12, "true" -> true,
  // "ff6a00" -> "#ff6a00" (URLs can't easily contain "#").
  function coerce(key, raw) {
    if (raw === 'true') return true;
    if (raw === 'false') return false;
    if (VARS[key] && VARS[key][1] && /^-?\d+(\.\d+)?$/.test(raw)) return parseFloat(raw);
    if ((key === 'textureScale' || key === 'textureOpacity') && /^\d*(\.\d+)?$/.test(raw)) return parseFloat(raw);
    if (/^[0-9a-f]{3}([0-9a-f]{3})?([0-9a-f]{2})?$/i.test(raw) &&
        ['accent', 'accent2', 'alert', 'text', 'textDim', 'fill', 'textureColor', 'strokeColor', 'frameColor'].includes(key)) {
      return '#' + raw;
    }
    return raw;
  }

  function readData(panel) {
    const out = {};
    const d = panel.dataset;
    if (d.oswConfig) { try { Object.assign(out, JSON.parse(d.oswConfig)); } catch (e) { console.warn('[OSWPanels] bad data-osw-config JSON', e); } }
    for (const key of KEYS) if (d[key] !== undefined) out[key] = coerce(key, d[key]);
    if (d.effectOptions) { try { out.effectOptions = JSON.parse(d.effectOptions); } catch (e) { console.warn('[OSWPanels] bad data-effect-options JSON', e); } }
    return out;
  }

  function readURL(search) {
    const out = {};
    const params = new URLSearchParams(search === undefined ? global.location.search : search);
    for (const key of KEYS) if (params.has(key)) out[key] = coerce(key, params.get(key));
    if (params.has('tex')) out.texture = params.get('tex');   // short alias
    if (params.has('effectOptions')) {                         // JSON, e.g. {"count":2}
      try { out.effectOptions = JSON.parse(params.get('effectOptions')); }
      catch (e) { console.warn('[OSWPanels] bad effectOptions JSON in URL', e); }
    }
    return out;
  }

  /* ------------------------------------------------------------------
     MOTION
     ------------------------------------------------------------------ */
  function cssTime(el, name, fallback) {
    const v = getComputedStyle(el).getPropertyValue(name).trim();
    if (!v) return fallback;
    return v.endsWith('ms') ? parseFloat(v) : v.endsWith('s') ? parseFloat(v) * 1000 : fallback;
  }
  const wait = (ms) => new Promise(r => setTimeout(r, ms));

  // Each call gets a ticket. If show() is called again before the first
  // finishes, the old one sees its ticket is stale and stops cleaning up.
  async function show(panel, motion) {
    if (motion) apply(panel, { motion });
    const ticket = (panel._oswTicket = (panel._oswTicket || 0) + 1);
    panel.hidden = false;
    panel.classList.remove('is-leaving', 'is-entering');
    const staggers = panel.querySelectorAll('.osw-stagger');
    staggers.forEach((el, i) => el.style.setProperty('--i', i));
    void panel.offsetWidth;            // force a reflow so the animation restarts
    panel.classList.add('is-entering');
    // Effects can react to the entrance (shine sweeps once as it lands).
    const fx = panel._osw && panel._osw.effect;
    if (fx && typeof fx.onShow === 'function') {
      setTimeout(() => { if (panel._oswTicket === ticket) fx.onShow(); }, cssTime(panel, '--osw-in-ms', 650) * 0.5);
    }
    await wait(cssTime(panel, '--osw-in-ms', 650) * 0.45 + staggers.length * 140 + 400);
    if (panel._oswTicket === ticket) panel.classList.remove('is-entering');
  }

  async function hide(panel, motion) {
    if (motion) apply(panel, { motion });
    const ticket = (panel._oswTicket = (panel._oswTicket || 0) + 1);
    panel.classList.remove('is-entering');
    void panel.offsetWidth;
    panel.classList.add('is-leaving');
    await wait(cssTime(panel, '--osw-out-ms', 450));
    if (panel._oswTicket === ticket) {
      panel.hidden = true;
      panel.classList.remove('is-leaving');
    }
  }

  // Info Panel's slide rotation: fade content out, change it, fade it in.
  // The frame stays solid the whole time.
  async function swap(panel, changeFn) {
    panel.classList.add('is-swapping');
    await wait(cssTime(panel, '--osw-swap-ms', 450));
    try { await changeFn(); } finally { panel.classList.remove('is-swapping'); }
  }

  /* ------------------------------------------------------------------
     FIT TEXT (from Info Panel) — shrink 1px at a time until it fits
     ------------------------------------------------------------------ */
  function fitText(el, opts) {
    const o = Object.assign({ max: 26, min: 14, reserved: 0, container: el.parentElement }, opts);
    let px = o.max;
    el.style.fontSize = px + 'px';
    const room = o.container.clientWidth - o.reserved;
    while (el.scrollWidth > room && px > o.min) {
      px -= 1;
      el.style.fontSize = px + 'px';
    }
    return px;
  }

  /* ------------------------------------------------------------------
     UTILITIES (from Donor Ticker + Info Panel)
     ------------------------------------------------------------------ */
  const CURRENCY = { USD: '$', CAD: 'CA$', AUD: 'A$', NZD: 'NZ$', EUR: '€', GBP: '£', JPY: '¥', CNY: '¥',
    RMB: '¥', KRW: '₩', INR: '₹', BRL: 'R$', MXN: 'MX$', CHF: 'CHF', SEK: 'kr', NOK: 'kr', DKK: 'kr',
    PLN: 'zł', CZK: 'Kč', HUF: 'Ft', TRY: '₺', ZAR: 'R', SGD: 'S$', HKD: 'HK$', TWD: 'NT$', THB: '฿',
    MYR: 'RM', IDR: 'Rp', PHP: '₱', ILS: '₪', AED: 'د.إ', SAR: '﷼', QAR: '﷼' };

  const loadedFonts = new Set();
  const util = {
    // Any computed CSS color string -> {r, g, b, a} (v1.5).
    parseColor(css) { return parseColor(css); },
    hexToRgb(hex) {
      let s = String(hex || '').trim().replace('#', '');
      if (s.length === 3) s = s.split('').map(c => c + c).join('');
      if (s.length !== 6) return { r: 0, g: 0, b: 0 };
      const n = parseInt(s, 16);
      return { r: (n >> 16) & 255, g: (n >> 8) & 255, b: n & 255 };
    },
    hexToRgba(hex, alpha) {
      const { r, g, b } = util.hexToRgb(hex);
      const a = Math.max(0, Math.min(1, Number(alpha)));
      return `rgba(${r}, ${g}, ${b}, ${isNaN(a) ? 1 : a})`;
    },
    currencySymbol(code) {
      const c = String(code || '').trim().toUpperCase();
      return c ? (CURRENCY[c] || c) : '$';
    },
    formatMoney(amount, code) {
      const n = typeof amount === 'number' ? amount : parseFloat(String(amount ?? '').replace(',', '.'));
      return util.currencySymbol(code) + (isNaN(n) ? 0 : n).toFixed(2);
    },
    // Adds a Google Fonts <link> once per family. System fonts: don't call this.
    loadGoogleFont(family, weights = '400;600;700;900') {
      if (!family || loadedFonts.has(family)) return;
      loadedFonts.add(family);
      const link = document.createElement('link');
      link.rel = 'stylesheet';
      link.href = `https://fonts.googleapis.com/css2?family=${encodeURIComponent(family).replace(/%20/g, '+')}:wght@${weights}&display=swap`;
      document.head.appendChild(link);
    }
  };

  /* ------------------------------------------------------------------
     ASSETS
     ------------------------------------------------------------------ */
  const ICON = (body) => `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round">${body}</svg>`;
  const assets = {
    // Line icons from the Info Panel. Color follows the text color.
    icons: {
      clock: ICON('<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3.5 2"/>'),
      live: ICON('<path d="M4 12a8 8 0 0 1 16 0"/><path d="M7.5 12a4.5 4.5 0 0 1 9 0"/><circle cx="12" cy="12" r="1.6" fill="currentColor" stroke="none"/>'),
      wifi: ICON('<path d="M3 9a13 13 0 0 1 18 0"/><path d="M6.5 12.5a8 8 0 0 1 11 0"/><path d="M10 16a3.2 3.2 0 0 1 4 0"/><circle cx="12" cy="19" r="1.3" fill="currentColor" stroke="none"/>'),
      eye: ICON('<path d="M2 12s3.6-7 10-7 10 7 10 7-3.6 7-10 7-10-7-10-7Z"/><circle cx="12" cy="12" r="3"/>'),
      sun: ICON('<circle cx="12" cy="12" r="4.2"/><path d="M12 2.5v3M12 18.5v3M4.2 4.2l2.1 2.1M17.7 17.7l2.1 2.1M2.5 12h3M18.5 12h3M4.2 19.8l2.1-2.1M17.7 6.3l2.1-2.1"/>'),
      cloud: ICON('<path d="M7 18h10a4 4 0 0 0 .5-7.97A5.5 5.5 0 0 0 7.1 11 4 4 0 0 0 7 18Z"/>'),
      rain: ICON('<path d="M7 14h10a4 4 0 0 0 .5-7.97A5.5 5.5 0 0 0 7.1 7 4 4 0 0 0 7 14Z"/><path d="M8 18l-1 3M12 18l-1 3M16 18l-1 3"/>'),
      snow: ICON('<path d="M7 13h10a4 4 0 0 0 .5-7.97A5.5 5.5 0 0 0 7.1 6 4 4 0 0 0 7 13Z"/><path d="M12 16v6M9.5 18.5l5-3M9.5 15.5l5 3"/>'),
      storm: ICON('<path d="M7 12h10a4 4 0 0 0 .5-7.97A5.5 5.5 0 0 0 7.1 5 4 4 0 0 0 7 12Z"/><path d="M13 14l-3 5h3l-2 4"/>'),
      gift: ICON('<rect x="4" y="9" width="16" height="11" rx="1"/><path d="M4 13h16M12 9v11"/><path d="M12 9C9 9 8 6.5 9.5 5S12 5 12 9ZM12 9c3 0 4-2.5 2.5-4S12 5 12 9Z"/>'),
      star: ICON('<path d="M12 3.5l2.6 5.4 5.9.8-4.3 4.1 1 5.9L12 16.9 6.8 19.7l1-5.9L3.5 9.7l5.9-.8Z"/>'),
      chat: ICON('<path d="M4 5h16v11H8l-4 4V5Z"/><path d="M8.5 9.5h7M8.5 12.5h4"/>'),
      heart: ICON('<path d="M12 20s-7.5-4.6-9.5-9.2C1 7 3 4 6.3 4c2 0 3.4 1.1 4.7 2.7C12.3 5.1 13.7 4 15.7 4 19 4 21 7 20.5 10.8 18.5 15.4 12 20 12 20Z"/>'),
      follow: ICON('<circle cx="10" cy="8" r="3.5"/><path d="M3.5 19c0-3.6 3-6 6.5-6"/><path d="M17 8v6M14 11h6"/>'),
      trophy: ICON('<path d="M7 4h10v5a5 5 0 0 1-10 0V4Z"/><path d="M7 5H4a3 3 0 0 0 3 5M17 5h3a3 3 0 0 1-3 5"/><path d="M12 14v3M9 20h6M10 17h4v3h-4Z"/>'),
      crown: ICON('<path d="M3 8l4 3 5-6 5 6 4-3-1.5 10h-15L3 8Z"/>'),
      medal: ICON('<circle cx="12" cy="15" r="5"/><path d="M9 4l3 6 3-6M12 12v-2"/>'),
      calendarStar: ICON('<rect x="4" y="5" width="16" height="15" rx="1.5"/><path d="M4 9.5h16M8 3v3M16 3v3"/><path d="M12 12l1.1 2.2 2.4.3-1.75 1.7.4 2.4L12 17.4l-2.15 1.2.4-2.4L8.5 14.5l2.4-.3Z"/>')
    },

    // Full-color platform marks from the Info Panel.
    platformLogos: {
      Twitch: '<svg viewBox="0 0 112 112" xmlns="http://www.w3.org/2000/svg"><g transform="translate(0,112) scale(0.1,-0.1)"><path fill="#9146FF" d="M297 882 l-67 -68 0 -251 0 -252 83 -3 82 -3 5 -63 5 -63 65 66 64 65 56 0 55 0 123 123 122 122 0 198 0 197 -263 0 -263 0 -67 -68z"/><path fill="#ffffff" d="M838 746 l3 -150 -58 -58 -57 -58 -55 0 -55 0 -47 -51 -48 -52 -3 49 -3 49 -62 3 -63 3 0 203 0 203 7 7 7 6 215 -2 216 -3 3 -149z"/><path fill="#9146FF" d="M550 715 l0 -85 25 0 25 0 0 85 0 85 -25 0 -25 0 0 -85z"/><path fill="#9146FF" d="M700 715 l0 -85 25 0 25 0 0 85 0 85 -25 0 -25 0 0 -85z"/></g></svg>',
      Kick: '<svg viewBox="0 0 112 112" xmlns="http://www.w3.org/2000/svg"><g transform="translate(0,112) scale(0.1,-0.1)" fill="#53FC18"><path d="M210 560 l0 -400 130 0 130 0 0 85 0 85 45 0 45 0 0 -40 0 -40 45 0 45 0 0 -45 0 -45 130 0 130 0 0 134 0 135 -42 3 -43 3 3 43 4 42 -46 0 -46 0 0 40 0 40 43 0 42 0 0 42 0 43 43 3 42 3 0 135 0 134 -130 0 -130 0 0 -45 0 -45 -45 0 -45 0 0 -40 0 -40 -45 0 -45 0 0 85 0 85 -130 0 -130 0 0 -400z"/></g></svg>',
      YouTube: '<svg viewBox="0 0 28.57 20" xmlns="http://www.w3.org/2000/svg"><path d="M27.9727 3.12324C27.6435 1.89323 26.6768 0.926623 25.4468 0.597366C23.2197 0 14.285 0 14.285 0C14.285 0 5.35042 0 3.12323 0.597366C1.89323 0.926623 0.926623 1.89323 0.597366 3.12324C0 5.35042 0 10 0 10C0 10 0 14.6496 0.597366 16.8768C0.926623 18.1068 1.89323 19.0734 3.12323 19.4026C5.35042 20 14.285 20 14.285 20C14.285 20 23.2197 20 25.4468 19.4026C26.6768 19.0734 27.6435 18.1068 27.9727 16.8768C28.5701 14.6496 28.5701 10 28.5701 10C28.5701 10 28.5677 5.35042 27.9727 3.12324Z" fill="#FF0000"/><path d="M11.4253 14.2854L18.8477 10.0004L11.4253 5.71533V14.2854Z" fill="white"/></svg>'
    },

    // Brand accent per platform (Social Rotator BRAND + Info Panel alerts).
    brandColors: {
      twitch: '#9146FF', youtube: '#FF0000', kick: '#53FC18', x: '#000000', twitter: '#000000',
      instagram: '#E1306C', tiktok: '#EE1D52', discord: '#5865F2', facebook: '#1877F2',
      threads: '#000000', bluesky: '#1185FE', default: '#ffb300'
    },

    // "Twitch" / "X / Twitter" -> "twitch" / "x" (Social Rotator's matching rule)
    platformKey(name) {
      const s = String(name || '').toLowerCase();
      if (s.includes('twitter') || s === 'x' || s.includes('x /')) return 'x';
      return s.split(' ')[0];
    },
    brandColor(name) {
      return assets.brandColors[assets.platformKey(name)] || assets.brandColors.default;
    },
    // Inline <span> with a platform logo at a given pixel size.
    platformLogo(name, size = 18) {
      const svg = assets.platformLogos[name] ||
        assets.platformLogos[Object.keys(assets.platformLogos).find(k => k.toLowerCase() === String(name).toLowerCase())];
      return svg ? `<span class="osw-logo" style="display:inline-flex;vertical-align:-0.2em;width:${size}px;height:${size}px">${svg}</span>` : '';
    },
    // Icon from github.com/OSUPhoenix/SB-Social-Packs (icons/<name>.png).
    // Accepts "twitch", "gh:twitch", or "subfolder/twitch.png".
    socialIconUrl(name, ref = 'HEAD') {
      let path = String(name || '').replace(/^gh:/, '').trim().toLowerCase();
      if (!path.endsWith('.png')) path += '.png';
      const safe = path.split('/').map(encodeURIComponent).join('/');
      return `https://raw.githubusercontent.com/OSUPhoenix/SB-Social-Packs/${ref}/icons/${safe}`;
    }
  };

  /* ------------------------------------------------------------------
     ADAPTERS — your existing Streamer.bot settings -> one config object.
     The C# actions can keep sending exactly what they send today.
     ------------------------------------------------------------------ */
  function clean(obj) {
    Object.keys(obj).forEach(k => (obj[k] === undefined || obj[k] === null) && delete obj[k]);
    return obj;
  }

  // Info Panel snapshot (OSUP_InfoPanel event's data object).
  function fromInfoPanelSnapshot(s) {
    if (!s) return {};
    return clean({
      shape: s.PanelShape || 'chamfer-right',
      accent: s.PanelOutlineCss,
      fill: s.PanelBackgroundCss,
      texture: s.PanelPatternEnabled ? (s.PanelPatternStyle || 'hex-grid') : 'none',
      textureColor: s.PanelPatternColorCss,
      text: s.TextColorCss,
      textDim: s.TextColorDimCss,
      font: s.TextFontFamily,
      fontIsGoogle: s.TextIsGoogleFont,
      weight: s.TextFontWeight,
      strokeWidth: s.TextStrokeWidthCss,
      strokeColor: s.TextStrokeColorCss,
      textShadow: s.TextShadowCss
    });
  }

  // Donor Ticker "config" command payload.
  function fromDonorTicker(c) {
    if (!c) return {};
    const shadow = c.textShadowColor ? util.hexToRgba(c.textShadowColor, 0.8) : null;
    return clean({
      preset: c.stylePreset,
      accent: c.highlightColor,
      alert: c.alertColor,
      fill: c.backgroundColor ? util.hexToRgba(c.backgroundColor, c.cardOpacity ?? 1) : undefined,
      text: c.textColor,
      font: c.fontFamily,
      fontIsGoogle: c.fontIsGoogle,
      strokeWidth: c.showStroke ? 1 : 0,
      strokeColor: c.strokeColor,
      textShadow: c.textShadowEnabled === false ? 'none'
        : shadow ? `0 0 3px ${shadow}, 0 0 8px ${shadow}` : undefined
    });
  }

  // Social Rotator theme + the current item (brand color wins).
  function fromSocialRotator(theme, item) {
    theme = theme || {};
    const t = (item && item.theme) || {};
    return clean({
      shape: 'rounded-rect',
      radius: theme.radius,
      accent: t.accent || (item && item.platform ? assets.brandColor(item.platform) : theme.accent),
      fill: t.bg || theme.bg,
      text: t.text || theme.text,
      font: theme.fontFamily ? String(theme.fontFamily).split(',')[0].replace(/"/g, '').trim() : undefined
    });
  }

  /* ==================================================================
     EFFECTS — animated canvas fills
     ------------------------------------------------------------------
     An effect is a function (canvas, panel, options) that returns
     { set(options), refresh(), destroy() }. Register your own with
       OSWPanels.registerEffect('sparks', (canvas, panel, opts) => {...})
     and use it with  effect: "sparks".
     ================================================================== */
  const EFFECTS = {};
  const EFFECT_META = {};
  // meta.canvas: false  -> a CSS-only effect; apply() won't create a canvas.
  function registerEffect(name, factory, meta) {
    EFFECTS[kebab(name)] = factory;
    EFFECT_META[kebab(name)] = Object.assign({ canvas: true }, meta);
  }

  /* ---- hexglow: your Hex Glow bar, ported --------------------------
     Changes from hex-glow.js (logic is otherwise the same):
       - draws into the panel's .osw-panel__fx canvas, clipped to its shape
       - glowColor defaults to the panel's --osw-accent and follows it
       - coreColor defaults to the accent mixed toward white
     Every option from the original still works through effectOptions. */
  const HEX_DEFAULTS = {
    hexRadius: 17, gap: 2.5, orientation: 'pointy',
    tileColor: '#171717', tileVariance: 0.35, gapColor: '#060606',
    glowColor: null, coreColor: null, glowWidth: 2.4, bloom: 7, bloomStrength: 0.85,
    activeRatio: 0.55, minSpeed: 0.35, maxSpeed: 1.1, minLength: 0.5, maxLength: 1.1,
    pulse: 0.4, seed: 1337
  };

  function makeRandom(seed) {          // mulberry32: same seed, same "random" layout
    if (!seed) return Math.random;
    let a = seed >>> 0;
    return function () {
      a = (a + 0x6D2B79F5) >>> 0;
      let t = a;
      t = Math.imul(t ^ (t >>> 15), t | 1);
      t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
      return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
  }
  const lerp = (a, b, t) => a + (b - a) * t;
  const shade = ({ r, g, b }, amt) => {
    const f = (c) => Math.max(0, Math.min(255, Math.round(c + amt * 255)));
    return `rgb(${f(r)},${f(g)},${f(b)})`;
  };
  function hexCorners(cx, cy, radius, orientation) {
    const start = orientation === 'flat' ? 0 : -90, pts = [];
    for (let i = 0; i < 6; i++) {
      const ang = (start + 60 * i) * Math.PI / 180;
      pts.push({ x: cx + radius * Math.cos(ang), y: cy + radius * Math.sin(ang) });
    }
    return pts;
  }
  // Position t (0..6) around the outline; wraps so comets loop forever.
  function pointOnPerimeter(c, t) {
    t = ((t % 6) + 6) % 6;
    const i = Math.floor(t), f = t - i, a = c[i], b = c[(i + 1) % 6];
    return { x: lerp(a.x, b.x, f), y: lerp(a.y, b.y, f) };
  }
  function tracePerimeter(ctx, c, a, b) {
    const p = pointOnPerimeter(c, a);
    ctx.moveTo(p.x, p.y);
    for (let k = Math.floor(a) + 1; k < b; k++) { const q = c[((k % 6) + 6) % 6]; ctx.lineTo(q.x, q.y); }
    const q = pointOnPerimeter(c, b);
    ctx.lineTo(q.x, q.y);
  }
  // Resolve any CSS color (including var() results) to "rgb(...)" via the browser.
  function cssColor(panel, value) {
    const probe = document.createElement('span');
    probe.style.color = value;
    probe.style.display = 'none';
    panel.appendChild(probe);
    const out = getComputedStyle(probe).color;
    probe.remove();
    return out;
  }
  const HAS_FILTER = (() => {
    const ctx = document.createElement('canvas').getContext('2d');
    if (!ctx || !('filter' in ctx)) return false;
    ctx.filter = 'blur(2px)';
    return ctx.filter === 'blur(2px)';
  })();

  registerEffect('hexglow', function (canvas, panel, options) {
    const o = Object.assign({}, HEX_DEFAULTS, options);
    const ctx = canvas.getContext('2d');
    const tileCanvas = document.createElement('canvas');
    const glowCanvas = document.createElement('canvas');
    const smallCanvas = document.createElement('canvas');
    const reduceMotion = global.matchMedia && global.matchMedia('(prefers-reduced-motion: reduce)').matches;
    let hexes = [], w = 0, h = 0, dpr = 1, rafId = 0, glow = '#ff1414', core = '#ff8a7a';

    function colors() {
      glow = o.glowColor || cssColor(panel, 'var(--osw-accent)');
      core = o.coreColor || cssColor(panel, `color-mix(in srgb, ${glow}, #fff 45%)`);
    }

    function setup() {
      const rect = canvas.getBoundingClientRect();
      if (!rect.width || !rect.height) return;
      dpr = Math.min(global.devicePixelRatio || 1, 2);
      w = rect.width; h = rect.height;
      const pw = Math.round(w * dpr), ph = Math.round(h * dpr);
      for (const c of [canvas, tileCanvas, glowCanvas]) { c.width = pw; c.height = ph; }
      smallCanvas.width = Math.max(1, Math.round(pw / 6));
      smallCanvas.height = Math.max(1, Math.round(ph / 6));
      colors();
      buildGrid();
      drawTiles();
    }

    function buildGrid() {
      const r = o.hexRadius, rand = makeRandom(o.seed), S3 = Math.sqrt(3);
      const pointy = o.orientation !== 'flat';
      const sx = pointy ? S3 * r : 1.5 * r, sy = pointy ? 1.5 * r : S3 * r;
      const cols = Math.ceil(w / sx) + 2, rows = Math.ceil(h / sy) + 2;
      hexes = [];
      for (let row = -1; row < rows; row++) {
        for (let col = -1; col < cols; col++) {
          let cx = col * sx, cy = row * sy;
          if (pointy && (row & 1)) cx += sx / 2;
          if (!pointy && (col & 1)) cy += sy / 2;
          hexes.push({
            corners: hexCorners(cx, cy, r, o.orientation), cx, cy,
            shadeOffset: (rand() - 0.5) * o.tileVariance * 0.12,
            active: rand() < o.activeRatio, phase: rand() * 6,
            speed: lerp(o.minSpeed, o.maxSpeed, rand()), dir: rand() < 0.5 ? 1 : -1,
            length: lerp(o.minLength, o.maxLength, rand()),
            pulsePhase: rand() * Math.PI * 2, pulseSpeed: lerp(0.6, 1.6, rand())
          });
        }
      }
    }

    function drawTiles() {
      const t = tileCanvas.getContext('2d');
      t.setTransform(dpr, 0, 0, dpr, 0, 0);
      t.clearRect(0, 0, w, h);
      const base = util.hexToRgb(o.tileColor);
      const tileR = o.hexRadius - (o.gap / 2) / Math.cos(Math.PI / 6);
      for (const hx of hexes) {
        const pts = hexCorners(hx.cx, hx.cy, tileR, o.orientation);
        const g = t.createLinearGradient(0, hx.cy - tileR, 0, hx.cy + tileR);
        g.addColorStop(0, shade(base, hx.shadeOffset + 0.035));
        g.addColorStop(1, shade(base, hx.shadeOffset - 0.035));
        t.beginPath();
        pts.forEach((p, i) => (i ? t.lineTo(p.x, p.y) : t.moveTo(p.x, p.y)));
        t.closePath();
        t.fillStyle = g; t.fill();
        t.strokeStyle = 'rgba(255,255,255,0.035)'; t.lineWidth = 1; t.stroke();
      }
    }

    function drawGlow(time) {
      const g = glowCanvas.getContext('2d');
      g.setTransform(dpr, 0, 0, dpr, 0, 0);
      g.clearRect(0, 0, w, h);
      g.lineCap = 'round'; g.lineJoin = 'round';
      const PIECES = 6;
      for (const hx of hexes) {
        if (!hx.active) continue;
        const head = hx.phase + hx.dir * hx.speed * time;
        const bright = 1 - o.pulse + o.pulse * (0.5 + 0.5 * Math.sin(time * hx.pulseSpeed + hx.pulsePhase));
        for (let k = 0; k < PIECES; k++) {
          const from = head - hx.dir * hx.length * (1 - k / PIECES);
          const to = head - hx.dir * hx.length * (1 - (k + 1) / PIECES);
          g.beginPath();
          tracePerimeter(g, hx.corners, Math.min(from, to), Math.max(from, to));
          g.globalAlpha = Math.pow((k + 1) / PIECES, 1.6) * bright;
          g.strokeStyle = glow; g.lineWidth = o.glowWidth; g.stroke();
        }
        const coreFrom = head - hx.dir * hx.length * 0.25;
        g.beginPath();
        tracePerimeter(g, hx.corners, Math.min(coreFrom, head), Math.max(coreFrom, head));
        g.globalAlpha = bright; g.strokeStyle = core; g.lineWidth = o.glowWidth * 0.55; g.stroke();
      }
      g.globalAlpha = 1;
    }

    function composite() {
      const W = canvas.width, H = canvas.height;
      ctx.setTransform(1, 0, 0, 1, 0, 0);
      ctx.globalCompositeOperation = 'source-over';
      ctx.globalAlpha = 1;
      ctx.fillStyle = o.gapColor;
      ctx.fillRect(0, 0, W, H);
      ctx.drawImage(glowCanvas, 0, 0);       // light under the tiles...
      ctx.drawImage(tileCanvas, 0, 0);       // ...peeks through the gaps
      ctx.globalCompositeOperation = 'lighter';
      const blurPx = o.bloom * dpr;
      if (HAS_FILTER) {
        ctx.filter = `blur(${blurPx}px)`;     ctx.globalAlpha = o.bloomStrength;       ctx.drawImage(glowCanvas, 0, 0);
        ctx.filter = `blur(${blurPx / 3}px)`; ctx.globalAlpha = o.bloomStrength * 0.6; ctx.drawImage(glowCanvas, 0, 0);
        ctx.filter = 'none';
      } else {
        const s = smallCanvas.getContext('2d');
        s.clearRect(0, 0, smallCanvas.width, smallCanvas.height);
        s.drawImage(glowCanvas, 0, 0, smallCanvas.width, smallCanvas.height);
        ctx.imageSmoothingEnabled = true;
        ctx.globalAlpha = o.bloomStrength * 1.4;
        ctx.drawImage(smallCanvas, 0, 0, W, H);
      }
      ctx.globalAlpha = 1;
      ctx.globalCompositeOperation = 'source-over';
    }

    function frame(now) {
      if (w) { drawGlow(reduceMotion ? 0 : now / 1000); composite(); }
      rafId = requestAnimationFrame(frame);
    }

    const ro = new ResizeObserver(setup);
    ro.observe(canvas);
    setup();
    rafId = requestAnimationFrame(frame);

    return {
      set(newOptions) { Object.assign(o, newOptions); setup(); },
      refresh() { colors(); },
      destroy() { cancelAnimationFrame(rafId); ro.disconnect(); }
    };
  });

  /* ---- electric: crackling lightning along the panel edges (v1.2) ---
     HOW IT WORKS
     A lightning bolt is a line broken into short segments, with each
     joint pushed up or down at random. Redraw it with new random offsets
     ~18 times a second and it looks like it's crackling. Each bolt is
     drawn twice: a wide stroke with a blur for the glow, then a thin pale
     stroke on top for the hot core (the same two-pass idea Hex Glow uses).
     Every few frames a short branch forks off a random joint.

     OPTIONS (effectOptions)
       lines: 2          1 = through the middle, 2 = top + bottom edges
       inset: 0.07       how far from the edge the 2 lines run (0-0.5 of height)
       amplitude: 4      px of jitter on a 90px-tall panel (scales with height)
       segments: 26      joints across the width (more = finer crackle)
       fps: 18           redraws per second
       width: 1.6        px line width
       glow: 10          px blur of the glow pass
       branches: 0.35    chance per frame of a fork (0-1)
       color / core      default: panel accent / accent mixed with white  */
  const ELECTRIC_DEFAULTS = {
    lines: 2, inset: 0.07, amplitude: 4, segments: 26, fps: 18, width: 1.6, glow: 10,
    branches: 0.35, color: null, core: null
  };

  registerEffect('electric', function (canvas, panel, options) {
    const o = Object.assign({}, ELECTRIC_DEFAULTS, options);
    const ctx = canvas.getContext('2d');
    const reduceMotion = global.matchMedia && global.matchMedia('(prefers-reduced-motion: reduce)').matches;
    let w = 0, h = 0, dpr = 1, rafId = 0, last = 0, color = '#a855f7', core = '#fff';

    function colors() {
      color = o.color || cssColor(panel, 'var(--osw-accent)');
      core = o.core || cssColor(panel, `color-mix(in srgb, ${color}, #fff 70%)`);
    }
    function setup() {
      const rect = canvas.getBoundingClientRect();
      if (!rect.width || !rect.height) return;
      dpr = Math.min(global.devicePixelRatio || 1, 2);
      w = rect.width; h = rect.height;
      canvas.width = Math.round(w * dpr);
      canvas.height = Math.round(h * dpr);
      colors();
      draw();
    }

    // One bolt across the panel at height y. Returns its joints.
    function boltPoints(y) {
      const pts = [];
      // Jitter scales with panel height, so a small panel doesn't get
      // bolts big enough to run through its text.
      const amp = o.amplitude * Math.min(1.5, h / 90);
      const step = w / o.segments;
      for (let i = 0; i <= o.segments; i++) {
        // Ends stay pinned near the edge; the middle jitters the most.
        const edge = Math.min(1, Math.min(i, o.segments - i) / 3);
        pts.push({ x: i * step, y: y + (Math.random() * 2 - 1) * amp * edge });
      }
      return pts;
    }
    function strokePath(pts, lineWidth, style, blur) {
      ctx.beginPath();
      pts.forEach((p, i) => (i ? ctx.lineTo(p.x, p.y) : ctx.moveTo(p.x, p.y)));
      ctx.lineWidth = lineWidth;
      ctx.strokeStyle = style;
      ctx.shadowColor = style;
      ctx.shadowBlur = blur;
      ctx.stroke();
    }
    function branch(from, dirY) {
      const pts = [{ x: from.x, y: from.y }];
      let x = from.x, y = from.y;
      const n = 3 + Math.floor(Math.random() * 3);
      for (let i = 0; i < n; i++) {
        x += (Math.random() * 2 - 1) * 9;
        y += dirY * (2 + Math.random() * 3) * Math.min(1.5, h / 90);
        pts.push({ x, y });
      }
      return pts;
    }

    function draw() {
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      ctx.clearRect(0, 0, w, h);
      ctx.lineCap = 'round';
      ctx.lineJoin = 'round';
      ctx.globalCompositeOperation = 'lighter';        // overlapping light adds up
      const ys = o.lines === 1 ? [h / 2] : [h * o.inset, h * (1 - o.inset)];
      ys.forEach((y, i) => {
        const pts = boltPoints(y);
        strokePath(pts, o.width * 2.2, color, o.glow);     // glow pass
        strokePath(pts, o.width * 0.6, core, o.glow / 3);  // core pass
        if (Math.random() < o.branches) {
          const j = pts[1 + Math.floor(Math.random() * (pts.length - 2))];
          const dir = o.lines === 1 ? (Math.random() < .5 ? -1 : 1) : (i === 0 ? 1 : -1); // fork toward the middle
          const b = branch(j, dir);
          strokePath(b, o.width * 1.2, color, o.glow * .6);
          strokePath(b, o.width * 0.4, core, 0);
        }
      });
      ctx.shadowBlur = 0;
      ctx.globalCompositeOperation = 'source-over';
    }

    function frame(now) {
      // Only redraw o.fps times a second: lightning that changes every
      // screen refresh (60+/s) looks like noise instead of crackle.
      if (w && !reduceMotion && now - last > 1000 / o.fps) { last = now; draw(); }
      rafId = requestAnimationFrame(frame);
    }

    const ro = new ResizeObserver(setup);
    ro.observe(canvas);
    setup();
    rafId = requestAnimationFrame(frame);

    return {
      set(newOptions) { Object.assign(o, newOptions); setup(); },
      refresh() { colors(); },
      destroy() { cancelAnimationFrame(rafId); ro.disconnect(); }
    };
  });

  /* ---- trace: comets of light lapping the frame outline (v1.3) -----
     CSS does the drawing and the motion (section 8 of osw-panels.css).
     This factory adds the class, one <i class="osw-trace"> per comet
     inside the frame, and turns options into CSS variables. No canvas and
     no JavaScript per frame.

     OPTIONS (effectOptions)
       speed: 3000      ms per lap
       length: 90       px of tail
       count: 1         comets spaced evenly around the frame (1-4)
       direction: "cw"  "cw" clockwise or "ccw"
       dim: false       true = fade the rest of the frame so comets pop
       color: null      default: accent mixed with white
     Needs a visible frame: skins with no frame (edge, bevel, gate) or
     frameWidth 0 show nothing. A frameWidth of 3-4 shows it best. */
  const TRACE_DEFAULTS = { speed: 3000, length: 90, count: 1, direction: 'cw', dim: false, color: null };
  registerEffect('trace', function (canvas, panel, options) {
    const o = Object.assign({}, TRACE_DEFAULTS);
    const frame = panel.querySelector(':scope > .osw-panel__frame');
    let comets = [];
    function build(n) {
      comets.forEach(c => c.remove());
      comets = Array.from({ length: n }, (_, i) => {
        const c = document.createElement('i');
        c.className = 'osw-trace';
        c.style.setProperty('--i', i);
        frame.appendChild(c);
        return c;
      });
    }
    function set(next) {
      Object.assign(o, next);
      const count = Math.min(4, Math.max(1, Math.round(Number(o.count) || 1)));
      if (count !== comets.length) build(count);
      panel.style.setProperty('--osw-trace-ms', Math.max(200, Number(o.speed) || 3000) + 'ms');
      panel.style.setProperty('--osw-trace-len', Math.max(10, Number(o.length) || 90) + 'px');
      panel.style.setProperty('--osw-trace-count', count);
      if (o.color) panel.style.setProperty('--osw-trace-color', o.color);
      else panel.style.removeProperty('--osw-trace-color');
      panel.classList.toggle('osw-fx--trace-ccw', kebab(o.direction) === 'ccw');
      panel.classList.toggle('osw-fx--trace-dim', !!o.dim && o.dim !== 'false');
    }
    panel.classList.add('osw-fx--trace');
    set(options);
    return {
      set,
      refresh() {},                     // colors are CSS variables: nothing to redo
      destroy() {
        comets.forEach(c => c.remove());
        panel.classList.remove('osw-fx--trace', 'osw-fx--trace-ccw', 'osw-fx--trace-dim');
        ['--osw-trace-ms', '--osw-trace-len', '--osw-trace-count', '--osw-trace-color']
          .forEach(v => panel.style.removeProperty(v));
      }
    };
  }, { canvas: false });

  /* ---- shine: a glare band sliding across the panel (v1.4) ---------
     CSS does the drawing (section 8). This factory adds the element,
     turns options into CSS variables, and builds the easing curve that
     makes the band sweep, then wait.

     OPTIONS (effectOptions)
       mode: "loop"        "loop" = every interval, "once" = only on play()/show()
       interval: 4000      ms from one sweep's start to the next (loop mode)
       duration: 900       ms the band takes to cross the panel
       delay: 0            ms before the first sweep (loop mode). Stagger
                           several panels so they don't all flash together.
       width: 30           band width, % of the panel width
       angle: 20           degrees of slant (0 = vertical band)
       strength: 0.35      brightness, 0-1
       color: "#fff"       glare color (try the accent for a tinted sheen)
       layer: "front"      "front" = over the text like glass, "behind" = under it
       playOnShow: true    sweep once each time OSWPanels.show() brings it in */
  const SHINE_DEFAULTS = { mode: 'loop', interval: 4000, duration: 900, delay: 0, width: 30, angle: 20,
    strength: 0.35, color: null, layer: 'front', playOnShow: true };

  // An ease-in-out curve squeezed into the first `frac` of the time, then a
  // flat line to the end: "move, then hold". linear() takes points as
  // "output input%", so we sample the curve at 8 places.
  function sweepEasing(frac) {
    const pts = ['0 0%'];
    for (let i = 1; i <= 8; i++) {
      const t = i / 8;
      const eased = t < 0.5 ? 2 * t * t : 1 - 2 * (1 - t) * (1 - t);
      pts.push(`${eased.toFixed(3)} ${(t * frac * 100).toFixed(2)}%`);
    }
    if (frac < 1) pts.push('1 100%');
    return `linear(${pts.join(', ')})`;
  }

  // k for the shine background: 2 + band width (as a fraction of the panel)
  // + slack for the slant, since a slanted band is wider at one end.
  function bgK(widthPct, angleDeg) {
    return 2 + widthPct / 100 + Math.min(0.4, Math.abs(Math.tan(angleDeg * Math.PI / 180)) * 0.25);
  }

  registerEffect('shine', function (canvas, panel, options) {
    const o = Object.assign({}, SHINE_DEFAULTS);
    const el = document.createElement('i');
    el.className = 'osw-shine';
    el.addEventListener('animationend', () => el.classList.remove('is-shining'));

    function place() {
      // front: child of the panel, over everything. behind: inside the fill.
      const parent = o.layer === 'behind' ? panel.querySelector(':scope > .osw-panel__fill') : panel;
      if (el.parentNode !== parent) parent.appendChild(el);
    }
    function set(next) {
      Object.assign(o, next);
      const num = (v, d) => (isNaN(Number(v)) ? d : Number(v));
      const duration = Math.max(100, num(o.duration, 900));
      const interval = Math.max(duration, num(o.interval, 4000));
      const width = Math.min(100, Math.max(2, num(o.width, 30)));
      const css = {
        '--osw-shine-ms': interval + 'ms',
        '--osw-shine-sweep': duration + 'ms',
        '--osw-shine-delay': Math.max(0, num(o.delay, 0)) + 'ms',
        '--osw-shine-ease': sweepEasing(duration / interval),
        '--osw-shine-ease-once': sweepEasing(1),
        // Background size (k x panel width) so the band just clears both
        // edges: its center moves from W(k/2) to W(1 - k/2), and must get
        // past the edge by half a band, so k = 2 + band width (+ slant slack).
        // The band is then width/k % of the background; half of that is the stop.
        '--osw-shine-size': (bgK(width, num(o.angle, 20)) * 100).toFixed(1) + '%',
        '--osw-shine-half': (width / bgK(width, num(o.angle, 20)) / 2).toFixed(2) + '%',
        '--osw-shine-angle': num(o.angle, 20) + 'deg',
        '--osw-shine-strength': Math.min(1, Math.max(0, num(o.strength, 0.35)))
      };
      Object.entries(css).forEach(([k, v]) => panel.style.setProperty(k, v));
      if (o.color) panel.style.setProperty('--osw-shine-color', o.color);
      else panel.style.removeProperty('--osw-shine-color');
      panel.classList.toggle('osw-fx--shine-loop', o.mode !== 'once');
      place();
    }
    panel.classList.add('osw-fx--shine');
    set(options);

    return {
      set,
      refresh() {},
      // One sweep right now. In loop mode this also restarts the cycle, so
      // the next automatic sweep is a full interval later.
      play() {
        if (o.mode !== 'once') {
          el.style.animation = 'none';
          void el.offsetWidth;            // reflow, so the browser sees the reset
          el.style.animation = '';
          el.style.animationDelay = '0ms';
          return;
        }
        el.classList.remove('is-shining');
        void el.offsetWidth;
        el.classList.add('is-shining');
      },
      onShow() { if (o.playOnShow && o.playOnShow !== 'false') this.play(); },
      destroy() {
        el.remove();
        panel.classList.remove('osw-fx--shine', 'osw-fx--shine-loop');
        ['--osw-shine-ms', '--osw-shine-sweep', '--osw-shine-delay', '--osw-shine-ease', '--osw-shine-ease-once',
          '--osw-shine-size', '--osw-shine-half', '--osw-shine-angle', '--osw-shine-strength', '--osw-shine-color']
          .forEach(v => panel.style.removeProperty(v));
      }
    };
  }, { canvas: false });

  /* ==================================================================
     PARTICLE ENGINE (v1.5, extended in 1.6)
     ------------------------------------------------------------------
     One engine, many looks. A "particle preset" only describes how ONE
     particle is born, how it moves and (optionally) how it's drawn. The
     engine handles the rest: canvas, sizing, the frame loop, fading,
     culling, cleanup. Embers, confetti, coins, gems and image showers are
     all presets of this one engine, the way one DLL method serves many
     Streamer.bot actions.

     PARTS
       Ticker          one shared animation loop for every particle canvas
       ParticleSystem  one per panel (or per burst): a canvas + its particles
       sprites         pre-drawn images, so a frame is just fast image stamps
       launch / fall   shared "throw it" and "gravity + air" physics (1.6)
       presets         registered with registerParticles()
     ================================================================== */

  /* ---- Ticker: ONE requestAnimationFrame loop for all particles ------
     Ten canvases with their own loops means ten callbacks per frame. One
     shared loop does the same work with less overhead, and stops
     completely when nothing is subscribed. dt (seconds since the last
     frame) is capped at 0.05 so that after a tab or OBS source wakes up,
     particles don't all jump forward at once. */
  const Ticker = {
    subs: new Set(), raf: 0, last: 0,
    add(fn) { this.subs.add(fn); if (!this.raf) { this.last = performance.now(); this.raf = requestAnimationFrame(t => this.loop(t)); } },
    remove(fn) { this.subs.delete(fn); if (!this.subs.size && this.raf) { cancelAnimationFrame(this.raf); this.raf = 0; } },
    loop(now) {
      const dt = Math.min(0.05, (now - this.last) / 1000);
      this.last = now;
      this.subs.forEach(fn => fn(dt, now / 1000));
      this.raf = this.subs.size ? requestAnimationFrame(t => this.loop(t)) : 0;
    }
  };

  // Any computed CSS color -> {r,g,b,a}. Handles both "rgb(200, 30, 44)"
  // and "color(srgb 0.78 0.12 0.17 / 0.5)" (what color-mix() results
  // compute to: same color, 0-1 scale).
  function parseColor(css) {
    const n = (String(css).match(/[\d.]+/g) || ['0', '0', '0']).map(Number);
    const k = String(css).startsWith('color(') ? 255 : 1;
    return { r: Math.round(n[0] * k), g: Math.round(n[1] * k), b: Math.round(n[2] * k), a: n[3] !== undefined ? n[3] : 1 };
  }
  const lerpColor = (a, b, t) => ({ r: lerp(a.r, b.r, t), g: lerp(a.g, b.g, t), b: lerp(a.b, b.b, t) });
  const rgbStr = (c, a = 1) => `rgba(${Math.round(c.r)},${Math.round(c.g)},${Math.round(c.b)},${a})`;
  const mixRgb = (c, d, t) => lerpColor(c, d, t);
  const WHITE = { r: 255, g: 255, b: 255 }, BLACK = { r: 0, g: 0, b: 0 };
  const rand = (a, b) => a + Math.random() * (b - a);
  const pick = (list) => list[Math.floor(Math.random() * list.length)];

  /* ---- Sprites ---------------------------------------------------------
     Drawing a soft glowing dot (or a shaded coin, or a cut gem) from
     scratch for every particle, every frame, is slow. So each look is
     drawn ONCE onto a tiny off-screen canvas, and each frame just stamps
     that image with drawImage, which the GPU does very quickly. */
  const SPRITE_STEPS = 14;
  function spriteCanvas(size, paint) {
    const s = document.createElement('canvas');
    s.width = s.height = size;
    paint(s.getContext('2d'), size);
    return s;
  }
  function glowSprite(c) {
    return spriteCanvas(64, (g) => {
      const grad = g.createRadialGradient(32, 32, 0, 32, 32, 32);
      grad.addColorStop(0, rgbStr(c, 1));
      grad.addColorStop(0.16, rgbStr(c, 1));      // solid core
      grad.addColorStop(0.38, rgbStr(c, 0.32));   // glow
      grad.addColorStop(1, rgbStr(c, 0));
      g.fillStyle = grad;
      g.fillRect(0, 0, 64, 64);
    });
  }
  // A palette becomes SPRITE_STEPS glow sprites; a particle's age picks
  // one, so it can cool from white-hot to dark red as it rises.
  function paletteSprites(stops) {
    const out = [];
    for (let i = 0; i < SPRITE_STEPS; i++) {
      const t = i / (SPRITE_STEPS - 1) * (stops.length - 1);
      const j = Math.min(stops.length - 2, Math.floor(t));
      out.push(glowSprite(lerpColor(stops[j], stops[j + 1], t - j)));
    }
    return out;
  }

  // Named palettes. For embers they run hot -> cool (birth -> death); for
  // confetti they're just a set to pick from. CSS colors, so they can
  // follow the panel's accent.
  const PARTICLE_PALETTES = {
    fire: ['#fffbe8', '#ffd76a', '#ff8a1c', '#e2361c', '#4a0b05'],
    accent: ['#ffffff', 'color-mix(in srgb, var(--osw-accent), #fff 55%)', 'var(--osw-accent)', 'color-mix(in srgb, var(--osw-accent) 40%, #000)'],
    accent2: ['#ffffff', 'color-mix(in srgb, var(--osw-accent-2), #fff 55%)', 'var(--osw-accent-2)', 'color-mix(in srgb, var(--osw-accent-2) 40%, #000)'],
    spirit: ['#ffffff', '#b9f3ff', '#3fd0ff', '#2a4bff', '#0b0b40'],
    party: ['var(--osw-accent)', 'var(--osw-accent-2)', '#ffd23f', '#ffffff', '#3ee07a', '#3fa2ff'],
    gold: ['#fff3b0', '#ffc93c', '#d48a00', '#6e4100']        // highlight, face, rim, shadow
  };

  /* ---- Shared physics (1.6) -------------------------------------------
     launch(): give a new particle a starting point and a throw.
       "fountain"  up out of the top middle, spraying sideways
       "cannons"   two jets from the bottom corners, aimed up and inward
       "rain"      falling from above across the whole width
       "radial"    outward in every direction from the middle
     fall(): gravity pulls down; "drag" is air resistance, which bleeds a
     bit of speed every second so things float down instead of plummeting. */
  function launch(p, sys, mode) {
    const o = sys.o, w = sys.w, h = sys.h;
    const speed = o.speed * rand(0.55, 1);
    let ang;
    switch (kebab(mode)) {
      case 'cannons': {
        const left = Math.random() < 0.5;
        p.x = left ? rand(0, w * 0.06) : rand(w * 0.94, w);
        p.y = h;
        ang = (left ? -62 : -118) + rand(-14, 14);
        break;
      }
      case 'rain':
        p.x = rand(-sys.spillX, w + sys.spillX);
        p.y = -sys.spillTop + rand(0, 20);
        p.vx = rand(-30, 30); p.vy = rand(40, 140);
        return;
      case 'radial':
        p.x = w / 2 + rand(-w * 0.2, w * 0.2);
        p.y = h / 2;
        ang = rand(0, 360);
        break;
      default:                      // fountain
        p.x = w / 2 + rand(-w * 0.15, w * 0.15);
        p.y = 0;
        ang = -90 + rand(-38, 38);
    }
    const a = ang * Math.PI / 180;
    p.vx = Math.cos(a) * speed;
    p.vy = Math.sin(a) * speed;
  }
  function fall(p, dt, o) {
    p.vy += o.gravity * dt;
    const drag = 1 - (o.drag === undefined ? 1.2 : o.drag) * dt;
    p.vx *= drag;
    p.vy *= drag;
    p.x += p.vx * dt;
    p.y += p.vy * dt;
  }

  // How many particles a burst gets. With an amount (bits, dollars, gift
  // count), it grows with the size of the alert, but on a log scale: 10x
  // the amount gives about 30% more, so a huge cheer is bigger but the
  // screen never fills up.  amount 1 -> 0.5x, 100 -> 1x, 10000 -> 1.6x
  function burstCount(o, n) {
    if (n !== undefined) return n;
    const amount = Number(o.amount);
    if (!amount) return o.burst;
    return Math.round(o.burst * (0.4 + 0.3 * Math.log10(amount + 1)));
  }

  /* ---- ParticleSystem: one per panel (or per burst) ------------------
     The canvas is bigger than the panel by the spill amounts, so
     particles can leave it: spill (above), spillSide, spillBottom. It
     sits behind the panel (z-index -1; the panel's `isolation: isolate`
     keeps that "behind" local to the panel) or in front (layer: "front").
     Presets work in PANEL coordinates: (0,0) is the panel's top-left. */
  class ParticleSystem {
    constructor(panel, preset, options) {
      this.panel = panel;
      this.preset = preset;
      this.o = Object.assign({}, preset.defaults, options);
      this.list = [];
      this.carry = 0;                  // fractional particles left over between frames
      this.emitted = 0;
      this.done = new Promise(r => { this._resolve = r; });
      this.canvas = document.createElement('canvas');
      this.canvas.className = 'osw-particles';
      panel.appendChild(this.canvas);
      this.ctx = this.canvas.getContext('2d');
      this.reduceMotion = global.matchMedia && global.matchMedia('(prefers-reduced-motion: reduce)').matches;
      this.tick = (dt, time) => this.step(dt, time);
      this.ro = new ResizeObserver(() => this.resize());
      this.ro.observe(panel);
      this.apply();
      Ticker.add(this.tick);
    }
    apply() {
      const o = this.o;
      this.spillTop = Math.max(0, Number(o.spill) || 0);
      this.spillX = o.spillSide !== undefined ? Number(o.spillSide) : Math.round(this.spillTop * 0.35);
      this.spillBottom = o.spillBottom !== undefined ? Number(o.spillBottom) : 10;
      const st = this.canvas.style;
      st.left = -this.spillX + 'px';
      st.top = -this.spillTop + 'px';
      st.width = `calc(100% + ${this.spillX * 2}px)`;
      st.height = `calc(100% + ${this.spillTop + this.spillBottom}px)`;
      st.zIndex = o.layer === 'front' ? 5 : -1;
      this.colors();
      this.resize();
    }
    // Palette -> resolved colors (this.rgb), glow sprites (this.sprites),
    // and whatever extra sprites the preset makes in its init().
    colors() {
      const p = this.o.palette;
      let list = Array.isArray(p) ? p
        : (typeof p === 'string' && p.includes(',') ? p.split(',').map(s => s.trim())
        : PARTICLE_PALETTES[kebab(p)] || PARTICLE_PALETTES.fire);
      if (list.length < 2) list = [list[0], list[0]];
      this.rgb = list.map(c => parseColor(cssColor(this.panel, c)));
      this.sprites = this.preset.draw ? null : paletteSprites(this.rgb);
      if (this.preset.init) this.preset.init(this);
    }
    resize() {
      const r = this.panel.getBoundingClientRect();
      this.w = r.width; this.h = r.height;
      if (!this.w || !this.h) return;
      this.dpr = Math.min(global.devicePixelRatio || 1, 2);
      this.canvas.width = Math.round((this.w + this.spillX * 2) * this.dpr);
      this.canvas.height = Math.round((this.h + this.spillTop + this.spillBottom) * this.dpr);
    }
    spawn(burst) {
      if (this.list.length >= this.o.max) return;
      const p = { age: 0, life: 1, x: 0, y: 0, vx: 0, vy: 0, r: 2, burst: !!burst };
      this.preset.spawn(p, this, burst);
      this.list.push(p);
      this.emitted++;
    }
    burst(n) {
      const count = burstCount(this.o, n);
      for (let i = 0; i < count; i++) this.spawn(true);
    }
    step(dt, time) {
      if (!this.w || this.reduceMotion) {
        if (this.o.oneShot) this.destroy();   // nothing visible to wait for
        return;
      }
      const o = this.o;
      // Steady emission: rate is per second per 100px of panel width, so
      // a wide bar and a small badge look equally busy. `carry` keeps the
      // leftover fraction, so a rate of 0.5/frame still emits evenly.
      this.carry += (o.rate || 0) * (this.w / 100) * dt;
      while (this.carry >= 1) { this.spawn(false); this.carry -= 1; }

      const ctx = this.ctx;
      ctx.setTransform(this.dpr, 0, 0, this.dpr, this.spillX * this.dpr, this.spillTop * this.dpr);
      ctx.clearRect(-this.spillX, -this.spillTop, this.w + this.spillX * 2, this.h + this.spillTop + this.spillBottom);
      // 'lighter' adds light together, right for glows. Solid things
      // (confetti, coins) use normal painting, set by the preset.
      const blend = this.preset.blend || 'lighter';
      ctx.globalCompositeOperation = blend;

      const top = -this.spillTop, bottom = this.h + this.spillBottom;
      const left = -this.spillX, right = this.w + this.spillX;
      let alive = 0;
      for (let i = 0; i < this.list.length; i++) {
        const p = this.list[i];
        p.age += dt;
        const t = p.age / p.life;                 // 0 at birth -> 1 at death
        if (t >= 1 || p.y < top - 40 || p.y > bottom + 40 || p.x < left - 40 || p.x > right + 40) continue;
        this.preset.update(p, dt, t, time, this);
        // Fade in fast, fade out over the last 40% of life, and fade near
        // the canvas edges, so nothing is cut off on a hard line.
        const fade = Math.min(1, t / 0.06) * Math.min(1, (1 - t) / 0.4);
        const edge = Math.min(1, (p.y - top) / Math.max(12, this.spillTop * 0.5), (bottom - p.y) / 24,
          (p.x - left) / 16, (right - p.x) / 16);
        const alpha = fade * Math.max(0, edge) * (p.alpha === undefined ? 1 : p.alpha);
        if (alpha > 0.01) {
          ctx.globalAlpha = alpha;
          if (this.preset.draw) this.preset.draw(ctx, p, t, this);
          else {
            const sprite = this.sprites[Math.min(SPRITE_STEPS - 1, Math.floor(t * SPRITE_STEPS))];
            const g = p.r * 4.2;                  // sprite radius: core + glow
            if (p.stretch > 1) {
              // Streak: face the direction of travel and stretch the dot
              // along it. A cheap stand-in for motion blur.
              ctx.save();
              ctx.translate(p.x, p.y);
              ctx.rotate(Math.atan2(p.vy, p.vx));
              ctx.drawImage(sprite, -g * p.stretch, -g, g * 2 * p.stretch, g * 2);
              ctx.restore();
            } else {
              ctx.drawImage(sprite, p.x - g, p.y - g, g * 2, g * 2);
            }
          }
        }
        this.list[alive++] = p;                   // keep: compact the array in place
      }
      this.list.length = alive;                   // (no new array per frame = no garbage)
      ctx.globalAlpha = 1;
      ctx.globalCompositeOperation = 'source-over';
      // A one-shot burst cleans itself up once its last particle is gone.
      if (o.oneShot && this.emitted && !this.list.length) this.destroy();
    }
    set(next) { Object.assign(this.o, next); this.apply(); }
    destroy() {
      if (this.dead) return;
      this.dead = true;
      Ticker.remove(this.tick); this.ro.disconnect(); this.canvas.remove();
      this._resolve();
    }
  }

  /* ---- registerParticles: turn a preset into an effect AND a burst ----
     A preset is:
       defaults  its options
       spawn(p, sys, burst)            set p.x, p.y, p.vx, p.vy, p.r, p.life
       update(p, dt, t, time, sys)     move it; t = 0-1 through its life
       draw(ctx, p, t, sys)            optional: draw it yourself (ctx is
                                       already at panel coordinates, alpha set)
       init(sys)                       optional: pre-draw sprites from sys.rgb
       blend                           optional: 'source-over' for solid things
     Every preset also gets the engine's options: rate, max, spill,
     spillSide, spillBottom, layer, palette, burst, amount, playOnShow.
     Registering makes it usable two ways:
       effect: "name"                        always running (rate > 0)
       OSWPanels.burst(panel, "name", ...)   one shot, then cleans up    */
  const PARTICLE_COMMON = { rate: 4, max: 260, spill: 70, layer: 'behind', palette: 'fire', burst: 40, playOnShow: true };
  const PARTICLE_PRESETS = {};
  function registerParticles(name, preset) {
    const full = Object.assign({}, preset, { defaults: Object.assign({}, PARTICLE_COMMON, preset.defaults) });
    PARTICLE_PRESETS[kebab(name)] = full;
    registerEffect(name, function (canvas, panel, options) {
      const sys = new ParticleSystem(panel, full, options);
      return {
        set(next) { sys.set(next); },
        refresh() { sys.colors(); },             // accent changed: rebuild sprites
        play(n) { sys.burst(n); },               // a flare of extra particles
        onShow() { if (sys.o.playOnShow && sys.o.playOnShow !== 'false') sys.burst(); },
        destroy() { sys.destroy(); }
      };
    }, { canvas: false });                         // the engine makes its own (overflow) canvas
  }
  // One-shot: a temporary system that emits once and removes itself.
  function particleBurst(panel, name, options) {
    const sys = new ParticleSystem(panel, PARTICLE_PRESETS[name], Object.assign({}, options, { rate: 0, oneShot: true }));
    sys.burst(options && options.count);
    if (!sys.emitted) sys.destroy();
    return sys.done;
  }

  /* ---- embers --------------------------------------------------------
     Glowing flecks lifting off the panel, drifting and cooling as they
     rise. What makes them read as embers rather than dots:
       - they rise and SLOW DOWN (they cool and lose lift)
       - they wobble side to side on a sine wave, each at its own rhythm
       - they flicker, and change color from white-hot to dark red
       - now and then a fast, small, short-lived "spark" streaks out
     OPTIONS (plus the engine's)
       from: "top"     where they start: "top" band, "inside", or "bottom" edge
       rise: 45        px/s upward speed (each ember varies around it)
       size: 2.2       px core radius
       life: 2.6       seconds
       wind: 0         px/s sideways drift (+ = right)
       wobble: 1       side-to-side sway, 0 = none
       sparks: 0.08    share of fast sparks, 0-1                          */
  registerParticles('embers', {
    defaults: { from: 'top', rise: 45, size: 2.2, life: 2.6, wind: 0, wobble: 1, sparks: 0.08 },
    spawn(p, sys, burst) {
      const o = sys.o, w = sys.w, h = sys.h;
      const from = kebab(o.from);
      p.x = Math.random() * w;
      p.y = from === 'bottom' ? h - Math.random() * 4
          : from === 'inside' ? h * (0.25 + Math.random() * 0.75)
          : Math.random() * h * 0.35;                       // "top": the top third
      const spark = Math.random() < (burst ? 0.35 : o.sparks);
      const speed = o.rise * (0.6 + Math.random() * 0.8) * (spark ? 2.6 : 1) * (burst ? 1.7 : 1);
      p.vy = -speed;
      p.vx = (Math.random() * 2 - 1) * (burst ? 40 : 6);
      p.r = o.size * (0.55 + Math.random() * 0.7) * (spark ? 0.6 : 1) * (burst ? 1.15 : 1);
      p.life = o.life * (0.6 + Math.random() * 0.8) * (spark ? 0.45 : 1);
      p.phase = Math.random() * Math.PI * 2;                // own wobble/flicker rhythm
      p.freq = 1.6 + Math.random() * 2.4;
      p.spark = spark;
    },
    update(p, dt, t, time, sys) {
      const o = sys.o;
      p.vy *= 1 - 0.35 * dt;                                // losing lift as it cools
      p.vx *= 1 - 1.2 * dt;                                 // burst spread settles down
      const sway = Math.sin(p.age * p.freq + p.phase) * 14 * o.wobble;
      p.x += (p.vx + o.wind + sway) * dt;
      p.y += p.vy * dt;
      p.r *= 1 - 0.25 * dt;                                 // shrinking as it burns out
      p.alpha = 0.72 + 0.28 * Math.sin(time * 9 + p.phase * 3); // flicker
      // Sparks streak: the faster they go, the longer the streak.
      p.stretch = p.spark ? 1 + Math.hypot(p.vx, p.vy) / 55 : 1;
    }
  });

  /* ---- confetti (1.6) ----------------------------------------------------
     Paper rectangles thrown up, tumbling, fluttering down. The tumble is
     a fake 3D flip: squashing the rectangle's height by cos(flip) makes it
     look like it's turning over, and the back side is drawn a little
     darker. No real 3D needed.
     OPTIONS: mode ("cannons" | "fountain" | "rain" | "radial"), speed
     (px/s), gravity (px/s²), size (multiplier), life (s), palette ("party"). */
  const BURST_SPILL = { spill: 240, spillSide: 160, spillBottom: 200 };
  registerParticles('confetti', {
    blend: 'source-over',
    defaults: Object.assign({ rate: 0, layer: 'front', palette: 'party', burst: 120, max: 500,
      mode: 'cannons', speed: 720, gravity: 620, drag: 1.1, size: 1, life: 3.8 }, BURST_SPILL),
    spawn(p, sys) {
      launch(p, sys, sys.o.mode);
      const s = sys.o.size;
      p.w = rand(6, 12) * s; p.h = rand(3, 6) * s;
      p.c = pick(sys.rgb);
      p.rot = rand(0, Math.PI * 2); p.spin = rand(-8, 8);
      p.flip = rand(0, Math.PI * 2); p.flipSpeed = rand(6, 14);
      p.phase = rand(0, Math.PI * 2);
      p.life = sys.o.life * rand(0.7, 1.1);
    },
    update(p, dt, t, time, sys) {
      fall(p, dt, sys.o);
      p.x += Math.sin(p.age * 4 + p.phase) * 26 * dt;      // flutter
      p.rot += p.spin * dt;
      p.flip += p.flipSpeed * dt;
    },
    draw(ctx, p) {
      const c = Math.cos(p.flip);
      ctx.fillStyle = rgbStr(c < 0 ? mixRgb(p.c, BLACK, 0.3) : p.c);   // back side darker
      ctx.save();
      ctx.translate(p.x, p.y);
      ctx.rotate(p.rot);
      ctx.scale(1, Math.max(0.08, Math.abs(c)));
      ctx.fillRect(-p.w / 2, -p.h / 2, p.w, p.h);
      ctx.restore();
    }
  });

  /* ---- coins (1.6) -----------------------------------------------------
     Gold coins thrown up out of the panel, spinning. One coin sprite is
     drawn at startup (rim, face, inner ring, a star stamp, a shine); the
     spin is squashing that image sideways by cos(spin), the same trick
     as confetti's flip.
     OPTIONS: mode ("fountain"), speed, gravity, size (px radius), life,
     palette ("gold": highlight, face, rim, shadow). */
  function star(g, cx, cy, r, points) {
    g.beginPath();
    for (let i = 0; i < points * 2; i++) {
      const a = -Math.PI / 2 + i * Math.PI / points;
      const rr = i % 2 ? r * 0.45 : r;
      g.lineTo(cx + Math.cos(a) * rr, cy + Math.sin(a) * rr);
    }
    g.closePath();
  }
  registerParticles('coins', {
    blend: 'source-over',
    defaults: Object.assign({ rate: 0, layer: 'front', palette: 'gold', burst: 36, max: 200,
      mode: 'fountain', speed: 760, gravity: 1150, drag: 0.4, size: 9, life: 2.6 }, BURST_SPILL),
    init(sys) {
      const [hi, face, rim, dark] = [sys.rgb[0], sys.rgb[1] || sys.rgb[0], sys.rgb[2] || sys.rgb[1], sys.rgb[3] || BLACK];
      sys.coin = spriteCanvas(64, (g) => {
        g.fillStyle = rgbStr(dark); g.beginPath(); g.arc(32, 33, 30, 0, Math.PI * 2); g.fill();     // edge shadow
        g.fillStyle = rgbStr(rim); g.beginPath(); g.arc(32, 32, 30, 0, Math.PI * 2); g.fill();      // rim
        const grad = g.createRadialGradient(24, 22, 2, 32, 32, 26);
        grad.addColorStop(0, rgbStr(hi)); grad.addColorStop(0.55, rgbStr(face)); grad.addColorStop(1, rgbStr(rim));
        g.fillStyle = grad; g.beginPath(); g.arc(32, 32, 25, 0, Math.PI * 2); g.fill();             // face
        g.strokeStyle = rgbStr(rim, 0.8); g.lineWidth = 2; g.beginPath(); g.arc(32, 32, 19, 0, Math.PI * 2); g.stroke();
        g.fillStyle = rgbStr(mixRgb(rim, dark, 0.3), 0.9); star(g, 32, 33, 10, 5); g.fill();       // stamp
        g.fillStyle = 'rgba(255,255,255,.5)'; g.beginPath(); g.ellipse(22, 18, 9, 4, -0.6, 0, Math.PI * 2); g.fill(); // shine
      });
    },
    spawn(p, sys) {
      launch(p, sys, sys.o.mode);
      p.r = sys.o.size * rand(0.8, 1.2);
      p.spin = rand(0, Math.PI * 2); p.spinSpeed = rand(8, 16) * (Math.random() < 0.5 ? -1 : 1);
      p.rot = rand(-0.4, 0.4);
      p.life = sys.o.life * rand(0.8, 1.1);
    },
    update(p, dt, t, time, sys) { fall(p, dt, sys.o); p.spin += p.spinSpeed * dt; },
    draw(ctx, p, t, sys) {
      const sx = Math.max(0.1, Math.abs(Math.cos(p.spin)));
      ctx.save();
      ctx.translate(p.x, p.y);
      ctx.rotate(p.rot);
      ctx.scale(sx, 1);
      ctx.drawImage(sys.coin, -p.r, -p.r, p.r * 2, p.r * 2);
      ctx.restore();
    }
  });

  /* ---- gems (1.6) --------------------------------------------------------
     Cut gemstones that burst out and twinkle, colored by rarity TIER. Give
     it an amount (bits, dollars...) and it uses every tier up to the one
     that amount reaches: big alerts get rare colors and more gems.
     OPTIONS
       amount: 0     the alert's amount; picks the top tier and the count
       tiers         [[minAmount, color], ...] lowest first. Default is the
                     common gaming rarity scale: grey, green, blue, purple, gold
       mix: true     true = blend in lower tiers too; false = top tier only
       size: 12      px half-height of a gem
       mode, speed, gravity, life  as for coins                            */
  const GEM_TIERS = [[1, '#c9d1dc'], [100, '#3ee07a'], [1000, '#3fa2ff'], [5000, '#b45cff'], [10000, '#ffc83d']];
  function gemSprite(c) {
    const light = mixRgb(c, WHITE, 0.5), mid = c, dark = mixRgb(c, BLACK, 0.45), deep = mixRgb(c, BLACK, 0.65);
    return spriteCanvas(64, (g) => {
      // A classic cut gem seen from the side: a flat top (table), a short
      // sloped crown down to the widest line (girdle), then a long point
      // underneath (pavilion). Facets are just triangles in 4 shades.
      const T = [[20, 10], [44, 10]], G = [[6, 24], [58, 24]], B = [32, 60];
      const tri = (a, b, c2, col) => { g.fillStyle = rgbStr(col); g.beginPath(); g.moveTo(...a); g.lineTo(...b); g.lineTo(...c2); g.closePath(); g.fill(); };
      const M = [32, 24];
      tri(T[0], T[1], M, light);                       // table
      tri(G[0], T[0], M, mid);                         // crown, left
      tri(T[1], G[1], M, mixRgb(c, WHITE, 0.25));      // crown, right
      tri(G[0], M, B, dark);                           // pavilion, left
      tri(M, G[1], B, mid);                            // pavilion, right
      tri(G[0], [19, 24], B, deep);                    // outer facets for depth
      tri([45, 24], G[1], B, dark);
      g.strokeStyle = rgbStr(mixRgb(c, WHITE, 0.6), 0.9); g.lineWidth = 1.5; g.lineJoin = 'round';
      g.beginPath(); g.moveTo(...T[0]); g.lineTo(...T[1]); g.lineTo(...G[1]); g.lineTo(...B); g.lineTo(...G[0]); g.closePath(); g.stroke();
      g.fillStyle = 'rgba(255,255,255,.85)'; star(g, 26, 15, 4, 4); g.fill();   // glint
    });
  }
  registerParticles('gems', {
    blend: 'source-over',
    defaults: Object.assign({ rate: 0, layer: 'front', burst: 30, max: 240, amount: 0, tiers: GEM_TIERS, mix: true,
      mode: 'fountain', speed: 700, gravity: 950, drag: 0.5, size: 12, life: 2.8 }, BURST_SPILL),
    init(sys) {
      let tiers = sys.o.tiers;
      if (typeof tiers === 'string') { try { tiers = JSON.parse(tiers); } catch (e) { tiers = GEM_TIERS; } }
      sys.tiers = tiers.map(([min, color]) => {
        const c = parseColor(cssColor(sys.panel, color));
        return { min: Number(min), sprite: gemSprite(c), glow: glowSprite(mixRgb(c, WHITE, 0.3)) };
      });
      // Which tiers this burst uses: up to the highest one the amount reaches.
      const amount = Number(sys.o.amount) || 0;
      let top = sys.tiers.length - 1;
      if (amount) { top = 0; sys.tiers.forEach((tr, i) => { if (amount >= tr.min) top = i; }); }
      sys.useTiers = sys.o.mix && sys.o.mix !== 'false' ? sys.tiers.slice(0, top + 1) : [sys.tiers[top]];
    },
    spawn(p, sys) {
      launch(p, sys, sys.o.mode);
      // Higher tiers are rarer within a mixed burst: weight toward the top
      // tier, then each lower tier less often.
      const list = sys.useTiers;
      const i = list.length === 1 ? 0 : Math.min(list.length - 1, Math.floor(Math.pow(Math.random(), 0.6) * list.length));
      p.tier = list[i];
      p.r = sys.o.size * rand(0.75, 1.2);
      p.rot = rand(-0.5, 0.5); p.spin = rand(-2.5, 2.5);
      p.flip = rand(0, Math.PI * 2); p.flipSpeed = rand(2.5, 5);
      p.tw = rand(0, Math.PI * 2);
      p.life = sys.o.life * rand(0.8, 1.1);
    },
    update(p, dt, t, time, sys) { fall(p, dt, sys.o); p.rot += p.spin * dt; p.flip += p.flipSpeed * dt; },
    draw(ctx, p, t, sys) {
      // Soft colored glow behind (added light), then the gem itself.
      ctx.globalCompositeOperation = 'lighter';
      const a = ctx.globalAlpha;
      ctx.globalAlpha = a * 0.55;
      const g = p.r * 2.6;
      ctx.drawImage(p.tier.glow, p.x - g, p.y - g, g * 2, g * 2);
      ctx.globalAlpha = a;
      ctx.globalCompositeOperation = 'source-over';
      ctx.save();
      ctx.translate(p.x, p.y);
      ctx.rotate(p.rot);
      ctx.scale(Math.max(0.35, Math.abs(Math.cos(p.flip))), 1);   // a slow turn that catches the light
      ctx.drawImage(p.tier.sprite, -p.r, -p.r, p.r * 2, p.r * 2);
      ctx.restore();
    }
  });

  /* ---- images (1.6) ----------------------------------------------------
     Shower any picture: your own emote or logo art, or the OFFICIAL
     Twitch cheermote images. Twitch serves those through its Get
     Cheermotes API (one URL per tier); fetch them once with Streamer.bot,
     save the URLs, and pass them as src.
     OPTIONS
       src: ""      an image URL, a list of URLs, or "url1, url2"
       size: 28     px height (width follows each image's shape)
       mode, speed, gravity, life  as for coins
     Animated GIFs show only their first frame on a canvas; use PNG/WebP. */
  registerParticles('images', {
    blend: 'source-over',
    defaults: Object.assign({ rate: 0, layer: 'front', src: '', burst: 24, max: 200,
      mode: 'fountain', speed: 700, gravity: 950, drag: 0.5, size: 28, life: 2.8 }, BURST_SPILL),
    init(sys) {
      const src = sys.o.src;
      const urls = Array.isArray(src) ? src : String(src || '').split(',').map(s => s.trim()).filter(Boolean);
      if (!urls.length) console.warn('[OSWPanels] images burst: no src given');
      sys.imgs = urls.map(u => { const im = new Image(); im.decoding = 'async'; im.src = u; return im; });
    },
    spawn(p, sys) {
      launch(p, sys, sys.o.mode);
      p.img = sys.imgs.length ? pick(sys.imgs) : null;
      p.r = sys.o.size / 2 * rand(0.8, 1.2);
      p.rot = rand(-0.5, 0.5); p.spin = rand(-3, 3);
      p.flip = rand(0, Math.PI * 2); p.flipSpeed = rand(2, 4);
      p.life = sys.o.life * rand(0.8, 1.1);
    },
    update(p, dt, t, time, sys) { fall(p, dt, sys.o); p.rot += p.spin * dt; p.flip += p.flipSpeed * dt; },
    draw(ctx, p) {
      const im = p.img;
      if (!im || !im.complete || !im.naturalWidth) return;     // still loading, or failed
      const h = p.r * 2, w = h * im.naturalWidth / im.naturalHeight;
      ctx.save();
      ctx.translate(p.x, p.y);
      ctx.rotate(p.rot);
      ctx.scale(Math.max(0.3, Math.abs(Math.cos(p.flip))), 1);
      ctx.drawImage(im, -w / 2, -h / 2, w, h);
      ctx.restore();
    }
  });

  /* ==================================================================
     ALERT BURSTS (1.6)  —  OSWPanels.burst(panel, type, options)
     ------------------------------------------------------------------
     One-shot effects for the moment an alert lands. They run on top of
     whatever the panel's normal effect is, then clean themselves up.
       type     "confetti" | "coins" | "gems" | "images" | "embers" (any
                particle preset) | "shockwave" | "impact", or several at
                once: ["impact", "shockwave", "gems"] or "impact, gems"
       options  shared by every type in the call, e.g. { amount: 500 }
     Returns a Promise that resolves when everything has finished.

     From Streamer.bot, send something like
       { "event": "MyAlert", "burst": ["impact", "gems"], "amount": 500 }
     and in the widget:  OSWPanels.burst(panel, data.burst, data)
     ================================================================== */
  const BURSTS = {};
  function registerBurst(name, fn) { BURSTS[kebab(name)] = fn; }

  function burst(panel, type, options) {
    if (!panel) return Promise.resolve();
    upgrade(panel);
    const o = options || {};
    const types = (Array.isArray(type) ? type : String(type || '').split(',')).map(t => kebab(String(t).trim())).filter(Boolean);
    return Promise.all(types.map(t => {
      if (BURSTS[t]) return BURSTS[t](panel, o);
      if (PARTICLE_PRESETS[t]) return particleBurst(panel, t, o);
      console.warn(`[OSWPanels] Unknown burst "${t}". Known: ${Object.keys(BURSTS).concat(Object.keys(PARTICLE_PRESETS)).join(', ')}`);
      return Promise.resolve();
    }));
  }

  /* ---- shockwave ---------------------------------------------------------
     Rings of light expanding out from the panel's outline.
     The trick that makes it work on every shape: each ring is a COPY of
     the panel's own frame element. The copy gets the same CSS as the
     frame, so it's already cut into the right ring shape (hexagon, pill,
     ribbon...). We then just scale it up and fade it out. Behind it a
     filled copy of the shape (the "halo") does the same, more softly.
     Scaling is done separately in x and y, so the ring grows by the same
     number of pixels on every side even on a wide panel.
     OPTIONS: count 2, gap 140 (ms between rings), grow 46 (px), duration
     750 (ms), color (default: accent). Rings need a visible frame; the
     halo shows on any skin. */
  registerBurst('shockwave', function (panel, o) {
    const opt = Object.assign({ count: 2, gap: 140, grow: 46, duration: 750, color: null }, o.shockwave || {}, pickKeys(o, ['count', 'gap', 'grow', 'duration', 'color'], 'shock'));
    const r = panel.getBoundingClientRect();
    if (!r.width) return Promise.resolve();
    const sx = 1 + (opt.grow * 2) / r.width, sy = 1 + (opt.grow * 2) / r.height;
    const frame = panel.querySelector(':scope > .osw-panel__frame');
    const made = [];
    for (let i = 0; i < opt.count; i++) {
      const halo = document.createElement('i');
      halo.className = 'osw-halo';
      const ring = frame.cloneNode(false);          // shallow copy: no comets etc. inside
      ring.classList.add('osw-shockwave');
      for (const el of [halo, ring]) {
        el.style.setProperty('--osw-shock-sx', sx);
        el.style.setProperty('--osw-shock-sy', sy);
        el.style.setProperty('--osw-shock-ms', opt.duration + 'ms');
        el.style.animationDelay = (i * opt.gap) + 'ms';
        if (opt.color) el.style.setProperty('--osw-shock-color', opt.color);
        panel.appendChild(el);
        made.push(el);
      }
    }
    return new Promise(res => setTimeout(() => { made.forEach(el => el.remove()); res(); }, opt.duration + opt.count * opt.gap + 60));
  });

  /* ---- impact -------------------------------------------------------------
     A white flash over the panel and a quick shake, the "hit" of an alert.
     The shake animates the `translate` property, not `transform`, so it
     adds on top of any other movement instead of replacing it.
     OPTIONS: flash 0.8 (0-1 brightness), shake 6 (px), duration 420 (ms),
     color "#fff". */
  registerBurst('impact', function (panel, o) {
    const opt = Object.assign({ flash: 0.8, shake: 6, duration: 420, color: '#fff' }, o.impact || {}, pickKeys(o, ['flash', 'shake', 'duration', 'color'], 'impact'));
    const fl = document.createElement('i');
    fl.className = 'osw-flash';
    fl.style.setProperty('--osw-flash-a', opt.flash);
    fl.style.setProperty('--osw-flash-color', opt.color);
    panel.style.setProperty('--osw-impact-ms', opt.duration + 'ms');
    panel.style.setProperty('--osw-shake', opt.shake + 'px');
    panel.appendChild(fl);
    panel.classList.remove('is-impact');
    void panel.offsetWidth;                        // reflow, so a repeat hit restarts
    panel.classList.add('is-impact');
    return new Promise(res => setTimeout(() => { fl.remove(); panel.classList.remove('is-impact'); res(); }, opt.duration + 40));
  });

  // Options can be given per type ({ impact: { shake: 10 } }) or flat with
  // a prefix ({ impactShake: 10 }) so one flat Streamer.bot payload can
  // tune several bursts at once without the names clashing.
  function pickKeys(o, keys, prefix) {
    const out = {};
    keys.forEach(k => {
      const pk = prefix + k[0].toUpperCase() + k.slice(1);
      if (o[pk] !== undefined) out[k] = o[pk];
    });
    return out;
  }

  // Fire a panel's effect on demand (shine sweeps; particle effects flare).
  // Returns false if the panel's effect has nothing to play.
  function play(panel) {
    const fx = panel && panel._osw && panel._osw.effect;
    if (fx && typeof fx.play === 'function') { fx.play(); return true; }
    return false;
  }

  /* ------------------------------------------------------------------
     AUTO-START — every .osw-panel on the page gets set up on load
     ------------------------------------------------------------------ */
  function init(root) {
    const urlCfg = readURL();
    (root || document).querySelectorAll('.osw-panel').forEach(panel => {
      if (panel._osw || panel.dataset.oswManual !== undefined) return;
      const cfg = Object.assign({}, readData(panel), panel.dataset.oswUrl === 'off' ? {} : urlCfg);
      apply(panel, cfg);
    });
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', () => init());
  else init();

  global.OSWPanels = {
    VERSION, EFFECT_META, SHAPES, SKINS, TEXTURES, MASK_TEXTURES, PATTERNS, PALETTES, MOTIONS, PRESETS, EFFECTS,
    ICON_SHAPES, ICON_POSITIONS,
    apply, reset, upgrade, init, show, hide, swap, play, fitText, readURL, readData,
    registerEffect, registerParticles, PARTICLE_PALETTES, PARTICLE_PRESETS,
    burst, registerBurst, BURSTS, GEM_TIERS, normalize, assets, util,
    fromInfoPanelSnapshot, fromDonorTicker, fromSocialRotator
  };
})(window);
