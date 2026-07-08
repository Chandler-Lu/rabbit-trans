const AK = 'PUT_YOUR_BAIDU_AK_HERE';
const SK = 'PUT_YOUR_BAIDU_SK_HERE';

main().catch((error) => {
  console.error(error?.stack || String(error));
  process.exit(1);
});

async function main() {
  const input = await readInput();
  const tokenUrl = new URL('https://aip.baidubce.com/oauth/2.0/token');
  tokenUrl.searchParams.set('grant_type', 'client_credentials');
  tokenUrl.searchParams.set('client_id', AK);
  tokenUrl.searchParams.set('client_secret', SK);
  const tokenResponse = await fetch(tokenUrl);
  if (!tokenResponse.ok) throw new Error(`Baidu token HTTP ${tokenResponse.status}: ${await tokenResponse.text()}`);
  const tokenData = await tokenResponse.json();
  if (!tokenData.access_token) throw new Error('Baidu token response did not contain access_token');

  const ocrUrl = new URL('https://aip.baidubce.com/rest/2.0/ocr/v1/general_basic');
  ocrUrl.searchParams.set('access_token', tokenData.access_token);
  const body = new URLSearchParams();
  body.set('image', input.imageBase64 || '');
  body.set('language_type', 'CHN_ENG');
  body.set('detect_direction', 'true');
  body.set('detect_language', 'true');
  body.set('paragraph', 'false');
  body.set('probability', 'false');
  const response = await fetch(ocrUrl, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body
  });
  if (!response.ok) throw new Error(`Baidu OCR HTTP ${response.status}: ${await response.text()}`);
  const data = await response.json();
  const text = Array.isArray(data.words_result) ? data.words_result.map((item) => item.words || '').join('\n') : '';
  process.stdout.write(JSON.stringify({ text, raw: data }));
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
