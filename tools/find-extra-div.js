const fs = require('fs');
const file = 'fe/src/views/settings/GeneralSettingsTab.vue';
const s = fs.readFileSync(file, 'utf8');
const m = s.match(/<template>[\s\S]*<\/template>/);
if (!m) { console.log('no template'); process.exit(1); }
const tmpl = m[0];
let open = 0;
const tagRe = /<(\/)?([A-Za-z0-9\-_:]+)([^>]*)>/g;
let match;
while ((match = tagRe.exec(tmpl))) {
  const isClosing = !!match[1];
  const tag = match[2].toLowerCase();
  if (tag === 'div') {
    if (isClosing) open--;
    else open++;
    if (open < 0) {
      console.log('extra closing </div> at index', match.index);
      console.log('context:\n', tmpl.slice(Math.max(0, match.index - 120), match.index + 120));
      process.exit(0);
    }
  }
}
console.log('no extra closing found, final open count', open);
