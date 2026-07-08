const API_KEY = 'PUT_YOUR_OCRSPACE_API_KEY_HERE';

main().catch((error) => {
  console.error(error?.stack || String(error));
  process.exit(1);
});

async function main() {
  const input = await readInput();
  const body = new URLSearchParams();
  body.set('apikey', API_KEY);
  body.set('language', 'chs');
  body.set('isOverlayRequired', 'false');
  body.set('base64Image', input.imageDataUri || `data:image/png;base64,${input.imageBase64 || ''}`);
  const response = await fetch('https://api.ocr.space/parse/image', {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body
  });
  if (!response.ok) throw new Error(`OCR.Space HTTP ${response.status}: ${await response.text()}`);
  const data = await response.json();
  process.stdout.write(JSON.stringify({ text: data?.ParsedResults?.[0]?.ParsedText || '', raw: data }));
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
