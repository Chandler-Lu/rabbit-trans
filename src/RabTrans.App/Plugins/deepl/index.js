const API_KEY = 'PUT_YOUR_DEEPL_API_KEY_HERE';

main().catch((error) => {
  console.error(error?.stack || String(error));
  process.exit(1);
});

async function main() {
  const input = await readInput();
  const body = new URLSearchParams();
  body.set('text', input.text || '');
  if (input.from && input.from !== 'auto') body.set('source_lang', input.from.toUpperCase());
  body.set('target_lang', (input.to || 'zh-CN').toUpperCase());
  const response = await fetch('https://api-free.deepl.com/v2/translate', {
    method: 'POST',
    headers: { Authorization: `DeepL-Auth-Key ${API_KEY}` },
    body
  });
  if (!response.ok) throw new Error(`DeepL HTTP ${response.status}: ${await response.text()}`);
  const data = await response.json();
  process.stdout.write(JSON.stringify({ text: data?.translations?.[0]?.text || '' }));
}

function readInput() {
  return new Promise((resolve, reject) => {
    let raw = '';
    process.stdin.setEncoding('utf8');
    process.stdin.on('data', (chunk) => raw += chunk);
    process.stdin.on('end', () => {
      try { resolve(JSON.parse(raw.replace(/^\uFEFF/, '') || '{}')); } catch (error) { reject(error); }
    });
  });
}
