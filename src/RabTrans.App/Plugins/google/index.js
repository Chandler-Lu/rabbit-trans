main().catch((error) => {
  console.error(error?.stack || String(error));
  process.exit(1);
});

async function main() {
  const input = await readInput();
  const url = new URL('https://translate.googleapis.com/translate_a/single');
  url.searchParams.set('client', 'gtx');
  url.searchParams.set('sl', input.from || 'auto');
  url.searchParams.set('tl', input.to || 'zh-CN');
  url.searchParams.set('dt', 't');
  url.searchParams.set('q', input.text || '');
  const response = await fetch(url);
  if (!response.ok) throw new Error(`Google Translate HTTP ${response.status}: ${await response.text()}`);
  const data = await response.json();
  const text = Array.isArray(data?.[0]) ? data[0].map((item) => item?.[0] || '').join('') : '';
  process.stdout.write(JSON.stringify({ text }));
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
