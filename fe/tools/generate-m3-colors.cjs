/* CommonJS version for node execution in this repo (generate tonal samples) */
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
