const API_KEY = 'PUT_YOUR_DASHSCOPE_API_KEY_HERE';
const MODEL = 'qwen3.5-35b-a3b';
const ENABLE_THINKING = false;

main().catch((error) => {
  console.error(error?.stack || String(error));
  process.exit(1);
});

async function main() {
  const input = await readInput();
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
      enable_thinking: ENABLE_THINKING
    })
  });
  if (!response.ok) throw new Error(`DashScope HTTP ${response.status}: ${await response.text()}`);
  const data = await response.json();
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
