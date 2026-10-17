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
  const MODEL = config.model ?? 'qwen3.5-35b-a3b';
  const ENABLE_THINKING = config.enableThinking ?? false;
  if (typeof MODEL !== 'string' || !MODEL.trim()) throw new Error('model must be a non-empty string.');
  if (typeof ENABLE_THINKING !== 'boolean') throw new Error('enableThinking must be a boolean.');
  const extraBody = config.extraBody === undefined ? {} : config.extraBody;
  if (!extraBody || typeof extraBody !== 'object' || Array.isArray(extraBody))
    throw new Error('extraBody must be a JSON object.');
  if (Object.hasOwn(extraBody, 'messages')) throw new Error('extraBody cannot override messages.');
  const response = await fetch('https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions', {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${API_KEY}`,
      'Content-Type': 'application/json'
    },
    body: JSON.stringify({
      model: MODEL,
      messages: [
        {
          role: 'system',
          content: 'You are a professional translation engine. Please translate the text into a colloquial, professional, elegant, and fluent style, without any trace of machine translation. Only translate the text content, never interpret it.'
        },
        {
          role: 'user',
          content: `Translate into ${input.to}:\n${input.text || ''}`
        }
      ],
      enable_thinking: ENABLE_THINKING,
      ...extraBody
    })
  }).catch(() => { throw new Error('Translation request failed. Check the service configuration and network.'); });
  if (!response.ok) throw new Error(`DashScope HTTP ${response.status}`);
  const data = await response.json().catch(() => { throw new Error('Service returned invalid JSON.'); });
  const message = data?.choices?.[0]?.message || {};
  process.stdout.write(JSON.stringify({
    text: message.content || '',
    reasoning: message.reasoning_content || '',
    reasoningVisible: false,
    raw: data
  }));
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
