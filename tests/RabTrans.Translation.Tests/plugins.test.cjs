const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { EventEmitter } = require('node:events');
const root = path.resolve(__dirname, '../../src/RabTrans.App/Plugins/translation');
const plugins = [
  { name: 'DashScope', file: path.join(root, 'dashscope-qwen/index.js'), model: true },
  { name: 'DeepL', file: path.join(root, 'deepl/index.js') }
];
if (process.env.RABTRANS_PLUGIN_TEST_ROOT) {
  for (const folder of ['caiyun', 'deepseek', 'qwen']) plugins.push({
    name: folder, file: path.join(process.env.RABTRANS_PLUGIN_TEST_ROOT, folder, 'index.js'),
    model: folder !== 'caiyun', token: folder === 'caiyun'
  });
}
plugins.push({ name: 'Baidu OCR', file: path.resolve(root, '../ocr/baidu-general-basic/index.js'), ocr: true });
if (process.env.RABTRANS_OCR_TEST_ROOT) plugins.push({ name: 'Custom Baidu OCR', file: path.join(process.env.RABTRANS_OCR_TEST_ROOT, 'baidu-basic-ocr/index.js'), ocr: true });
function run(plugin, config, response = {}) {
  return new Promise((resolve, reject) => {
    const calls = [];
    let errors = '';
    const timer = setTimeout(() => reject(new Error('Plugin did not finish')), 3000);
    const finish = (code, output = '') => { clearTimeout(timer); resolve({ code, output, errors, calls }); };
    const stdin = new EventEmitter();
    stdin.setEncoding = () => {};
    const context = {
      URL, URLSearchParams,
      process: { stdin, stdout: { write: text => finish(0, text) }, exit: code => finish(code) },
      console: { error: error => { errors += String(error); } },
      fetch: async (url, options) => {
        calls.push({ url, options });
        const active = response.at && response.at !== calls.length ? {} : response;
        if (active.reject) throw new Error('private-secret');
        return { ok: true, status: 200,
          json: async () => ({ choices: [{ message: { content: 'translated' } }], translations: [{ text: 'translated' }], target: ['translated'], access_token: 'fixture-token', words_result: [{ words: 'translated' }] }),
          text: async () => 'private-secret', ...active };
      }
    };
    try {
      vm.runInNewContext(fs.readFileSync(plugin.file, 'utf8'), context, { filename: plugin.file });
      stdin.emit('data', JSON.stringify({ text: 'hello', from: 'en', to: 'zh-CN', requestId: 'test', imageBase64: 'AQID', config }));
      stdin.emit('end');
    } catch (error) { clearTimeout(timer); reject(error); }
  });
}
for (const plugin of plugins) {
  const config = { [plugin.token ? 'token' : 'apiKey']: 'fixture-key', ak: 'fixture-ak', sk: 'fixture-sk', model: 'fixture-model', enableThinking: false };
  test(`${plugin.name}: config drives credentials and request`, async () => {
    const result = await run(plugin, config);
    assert.equal(result.code, 0);
    assert.equal(JSON.parse(result.output).text, 'translated');
    if (plugin.ocr) {
      assert.equal(result.calls[0].url.searchParams.get('client_id'), 'fixture-ak');
      assert.equal(result.calls[0].url.searchParams.get('client_secret'), 'fixture-sk');
      assert.equal(result.calls[1].options.body.get('image'), 'AQID');
    } else assert.ok(Object.values(result.calls[0].options.headers).some(value => value.includes('fixture-key')));
    if (plugin.model) {
      const body = JSON.parse(result.calls[0].options.body);
      assert.equal(body.model, 'fixture-model');
      assert.equal(body.enable_thinking, false);
      assert.ok(body.messages[1].content.includes('hello'));
    }
  });
  test(`${plugin.name}: missing credentials fail before network`, async () => {
    const result = await run(plugin, {});
    assert.equal(result.code, 1);
    assert.equal(result.calls.length, 0);
    assert.match(result.errors, /config.json/);
  });
  test(`${plugin.name}: network and HTTP errors do not expose response values`, async () => {
    for (const failure of [{ ok: false, status: 401 }, { reject: true }]) {
      const result = await run(plugin, config, failure);
      assert.equal(result.code, 1);
      assert.ok(!result.errors.includes('private-secret'));
    }
  });
  if (plugin.ocr) {
    test(`${plugin.name}: arbitrary form parameters and reserved image`, async () => {
      const extraBody = { detect_direction: false, vendor: { list: [0, null, ''] } };
      const result = await run(plugin, { ...config, extraBody });
      assert.equal(result.code, 0);
      assert.equal(result.calls[1].options.body.get('detect_direction'), 'false');
      assert.deepEqual(JSON.parse(result.calls[1].options.body.get('vendor')), extraBody.vendor);
      for (const invalid of [{ image: 'replacement' }, [], null]) {
        const failed = await run(plugin, { ...config, extraBody: invalid });
        assert.equal(failed.code, 1);
        assert.equal(failed.calls.length, 0);
      }
      const failed = await run(plugin, config, { ok: false, status: 401, at: 2 });
      assert.equal(failed.code, 1);
      assert.ok(!failed.errors.includes('private-secret'));
    });
  }
  if (plugin.model) {
    test(`${plugin.name}: arbitrary extraBody values reach HTTP body`, async () => {
      const extraBody = { model: 'override', temperature: 0, enable_thinking: true,
        vendor_options: { array: [false, '', null, { '自定义': '值' }] }, stop: [] };
      const result = await run(plugin, { ...config, extraBody });
      assert.equal(result.code, 0);
      const body = JSON.parse(result.calls[0].options.body);
      for (const key of Object.keys(extraBody)) assert.deepEqual(body[key], extraBody[key]);
      assert.ok(body.messages[1].content.includes('hello'));
    });
    test(`${plugin.name}: invalid extraBody and message overrides are rejected`, async () => {
      for (const extraBody of [null, [], false, 'value', { messages: [] }]) {
        const result = await run(plugin, { ...config, extraBody });
        assert.equal(result.code, 1);
        assert.equal(result.calls.length, 0);
        assert.match(result.errors, /extraBody/);
      }
    });
    test(`${plugin.name}: string thinking flag is not treated as true`, async () => {
      const result = await run(plugin, { ...config, enableThinking: 'false' });
      assert.equal(result.code, 1);
      assert.equal(result.calls.length, 0);
    });
  }
}
