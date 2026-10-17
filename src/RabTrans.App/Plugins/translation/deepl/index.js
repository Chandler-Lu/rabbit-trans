main().catch((error) => {
  console.error(error?.stack || String(error));
  process.exit(1);
});

async function main() {
  const input = await readInput();
  const config = input.config;
  if (!config || typeof config !== 'object' || Array.isArray(config))
    throw new Error('config must be a JSON object.');
  if (typeof config.apiKey !== 'string' || !config.apiKey.trim())
    throw new Error('Configure apiKey in config.json.');
  const API_KEY = config.apiKey;
  const body = new URLSearchParams();
  body.set('text', input.text || '');
  const sourceLang = mapSourceLang(input.from);
  if (sourceLang) body.set('source_lang', sourceLang);
  body.set('target_lang', mapTargetLang(input.to || 'zh-CN'));
  const response = await fetch('https://api-free.deepl.com/v2/translate', {
    method: 'POST',
    headers: { Authorization: `DeepL-Auth-Key ${API_KEY}` },
    body
  }).catch(() => { throw new Error('Translation request failed. Check the service configuration and network.'); });
  if (!response.ok) throw new Error(`DeepL HTTP ${response.status}`);
  const data = await response.json().catch(() => { throw new Error('Service returned invalid JSON.'); });
  process.stdout.write(JSON.stringify({ text: data?.translations?.[0]?.text || '' }));
}

function mapSourceLang(lang) {
  if (!lang || lang === 'auto') return '';
  const normalized = normalizeLang(lang);
  const sourceMap = {
    'zh': 'ZH',
    'zh-cn': 'ZH',
    'zh-hans': 'ZH',
    'zh-tw': 'ZH',
    'zh-hant': 'ZH',
    'en': 'EN',
    'pt-br': 'PT',
    'pt-pt': 'PT'
  };
  return sourceMap[normalized] || normalized.split('-')[0].toUpperCase();
}

function mapTargetLang(lang) {
  const normalized = normalizeLang(lang || 'zh-CN');
  const targetMap = {
    'zh': 'ZH-HANS',
    'zh-cn': 'ZH-HANS',
    'zh-hans': 'ZH-HANS',
    'zh-tw': 'ZH-HANT',
    'zh-hant': 'ZH-HANT',
    'en': 'EN-US',
    'en-us': 'EN-US',
    'en-gb': 'EN-GB',
    'pt': 'PT-PT',
    'pt-br': 'PT-BR',
    'pt-pt': 'PT-PT'
  };
  return targetMap[normalized] || normalized.split('-')[0].toUpperCase();
}

function normalizeLang(lang) {
  return String(lang || '').trim().replace('_', '-').toLowerCase();
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
