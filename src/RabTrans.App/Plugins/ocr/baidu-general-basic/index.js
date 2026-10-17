// RabTrans config protocol v1
main().catch((error) => {
  console.error(error?.stack || String(error));
  process.exit(1);
});

async function main() {
  const input = await readInput();
  const config = input.config;
  if (!config || typeof config !== 'object' || Array.isArray(config))
    throw new Error('config must be a JSON object.');
  const AK = config.ak;
  if (typeof AK !== 'string' || !AK.trim()) throw new Error('Configure ak in config.json.');
  const SK = config.sk;
  if (typeof SK !== 'string' || !SK.trim()) throw new Error('Configure sk in config.json.');
  const extraBody = config.extraBody === undefined ? {} : config.extraBody;
  if (!extraBody || typeof extraBody !== 'object' || Array.isArray(extraBody))
    throw new Error('extraBody must be a JSON object.');
  if (Object.hasOwn(extraBody, 'image')) throw new Error('extraBody cannot override image.');
  const tokenUrl = new URL('https://aip.baidubce.com/oauth/2.0/token');
  tokenUrl.searchParams.set('grant_type', 'client_credentials');
  tokenUrl.searchParams.set('client_id', AK);
  tokenUrl.searchParams.set('client_secret', SK);
  const tokenResponse = await fetch(tokenUrl).catch(() => { throw new Error('OCR authentication request failed. Check configuration and network.'); });
  if (!tokenResponse.ok) throw new Error(`Baidu token HTTP ${tokenResponse.status}`);
  const tokenData = await tokenResponse.json().catch(() => { throw new Error('OCR authentication returned invalid JSON.'); });
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
  for (const [key, value] of Object.entries(extraBody))
    body.set(key, typeof value === 'string' ? value : JSON.stringify(value));
  const response = await fetch(ocrUrl, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body
  }).catch(() => { throw new Error('Service request failed. Check the service configuration and network.'); });
  if (!response.ok) throw new Error(`Baidu OCR HTTP ${response.status}`);
  const data = await response.json().catch(() => { throw new Error('Service returned invalid JSON.'); });
  if (data.error_code) throw new Error('Baidu OCR request failed. Check credentials and OCR parameters.');
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
