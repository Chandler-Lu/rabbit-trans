const API_KEY = 'PUT_YOUR_OCR_API_KEY_HERE';
const ENDPOINT = 'https://example.com/ocr';

main().catch((error) => {
  console.error(error?.stack || String(error));
  process.exit(1);
});

async function main() {
  const input = await readInput();
  const response = await fetch(ENDPOINT, {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${API_KEY}`,
      'Content-Type': 'application/json'
    },
    body: JSON.stringify({
      image: input.imageBase64 || '',
      mime_type: input.mimeType || 'image/png',
      request_id: input.requestId || ''
    })
  });
  if (!response.ok) throw new Error(`Custom OCR HTTP ${response.status}: ${await response.text()}`);
  const data = await response.json();
  process.stdout.write(JSON.stringify({ text: data.text || '', raw: data }));
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
