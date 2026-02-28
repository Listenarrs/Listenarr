const fs = require('fs');
const path = require('path');
const file = path.join(__dirname, '..', 'fe', 'src', 'views', 'settings', 'GeneralSettingsTab.vue');
const s = fs.readFileSync(file, 'utf8');
const m = s.match(/<template>[\s\S]*<\/template>/);
if (!m) { console.log('no template block found'); process.exit(1); }
const tmpl = m[0];
const tagRe = /<(\/)?([A-Za-z0-9\-_:]+)([^>]*)>/g;
const selfClosingTags = new Set(['input','br','img','hr']);
const stack = [];
let match;
while ((match = tagRe.exec(tmpl))) {
  const isClosing = !!match[1];
  const tag = match[2];
  const raw = match[0];
  const selfClose = /\/>\s*$/.test(raw) || selfClosingTags.has(tag.toLowerCase()) || (/<\s*Ph[A-Za-z0-9]*\b/.test(raw) && /\/>\s*$/.test(raw));
  // verbose log
  console.log('TAG', {index: match.index, tag, isClosing, raw});
  if (isClosing) {
    if (stack.length === 0) {
      console.log('Unmatched closing tag:', tag, 'at pos', match.index);
      process.exit(2);
    }
    const last = stack.pop();
    console.log(' POP expected', last, '-> got', tag);
    if (last !== tag) {
      console.log('Mismatched closing tag:', tag, 'expected', last, 'at pos', match.index);
      console.log('Current stack:', stack);
      const ctxStart = Math.max(0, match.index - 200);
      const ctxEnd = Math.min(tmpl.length, match.index + 200);
      console.log('Context:...\n', tmpl.slice(ctxStart, ctxEnd), '\n...');
      process.exit(3);
    }
  } else if (!selfClose) {
    stack.push(tag);
    console.log(' PUSH', tag, 'stack depth', stack.length);
  }
}
if (stack.length) {
  console.log('Unclosed tags at end:', stack);
  process.exit(4);
}
console.log('No mismatches found');
