/*
  generate-m3-colors.js
  Simple script to generate M3 tonal palette CSS variables from a seed color using
  @material/material-color-utilities. Run from project root:

  cd fe
  npm install @material/material-color-utilities
  node tools/generate-m3-colors.js --seed #2196f3 --out m3-generated.css

  The script prints CSS variable definitions mapping key roles to tones. Use these
  variables to replace the approximations in `src/assets/base.css` if you want an
  accurate tonal system that follows Material 3.
*/

const fs = require('fs')
const path = require('path')
const { argbFromHex, hexFromArgb } = require('@material/material-color-utilities').argb
const palettes = require('@material/material-color-utilities').palettes
const argv = require('minimist')(process.argv.slice(2))

const seed = (argv.seed || '#2196f3').trim()
const out = argv.out || 'm3-generated.css'

function tonalPalette(hex) {
  const argb = argbFromHex(hex)
  const tonal = palettes.tonalPaletteFromArgb(argb)
  // tonal.asList(): index 0-100 corresponds to tones (not direct indexes), but utilities provide mapping
  // We pull common tones: 40=primary, 100=primaryContainer, 100? adjust as needed
  const palette = {};
  const tones = [0,10,20,30,40,50,60,70,80,90,95,99];
  tones.forEach((t) => {
    const c = palettes.tone(tonal, t)
    palette[`t${t}`] = hexFromArgb(c).toUpperCase()
  })
  return palette
}

const palette = tonalPalette(seed)
let css = `/* Generated from seed ${seed} */\n:root {\n`;
css += `  /* primary tonal samples */\n`;
css += `  --m3-primary-40: ${palette.t40};\n`;
css += `  --m3-primary-90: ${palette.t90};\n`;
css += `  --m3-primary-100: ${palette.t99};\n`;
css += `}\n`;

fs.writeFileSync(path.join(process.cwd(), out), css)
console.log(`Wrote ${out} (seed ${seed})`)
