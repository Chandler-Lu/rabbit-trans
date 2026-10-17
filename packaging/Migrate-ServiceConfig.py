"""One-time config migration of this installation's translation or Baidu OCR plugins.

Reads credentials locally; never prints them. Does not evaluate JavaScript.
Backups must be outside the plugin tree. Run with the new RabTrans executable.
"""
import argparse
import ast
from datetime import datetime
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile

MARKER = '// RabTrans config protocol v1\n'
OCR_SERVICES = {'baidu-basic-ocr': {'AK': 'ak', 'SK': 'sk'}}
SERVICES = {'caiyun': {'TOKEN': 'token'},
            'deepseek': {'API_KEY': 'apiKey', 'MODEL': 'model', 'ENABLE_THINKING': 'enableThinking'},
            'qwen': {'API_KEY': 'apiKey', 'MODEL': 'model', 'ENABLE_THINKING': 'enableThinking'}}


def transform(script, fields):
    config = {}
    declarations = []
    for constant, field in fields.items():
        pattern = rf'^const {constant}\s*=\s*(.+);[ \t]*\r?\n'
        match = re.search(pattern, script, re.M)
        if not match:
            raise ValueError('Expected parameter declaration was not found; migration stopped.')
        raw = match[1].strip()
        try:
            if raw in ('true', 'false'):
                value = raw == 'true'
            elif re.fullmatch(r"'(?:[^'\\]|\\.)*'|\"(?:[^\"\\]|\\.)*\"", raw):
                value = json.loads(raw) if raw.startswith('"') else ast.literal_eval(raw)
            else:
                raise ValueError()
        except (ValueError, SyntaxError):
            raise ValueError('Parameter is not a supported literal; migration stopped.') from None
        config[field] = value
        script = script[:match.start()] + script[match.end():]
        default = ' ?? false' if field == 'enableThinking' else ''
        declarations.append(f'  const {constant} = config.{field}{default};')
        if field == 'enableThinking':
            declarations.append(f"  if (typeof {constant} !== 'boolean') throw new Error('{field} must be a boolean.');")
        else:
            declarations.append(f"  if (typeof {constant} !== 'string' || !{constant}.trim()) throw new Error('Configure {field} in config.json.');")
    anchor = '  const input = await readInput();'
    if script.count(anchor) != 1:
        raise ValueError('Unexpected plugin input handler; migration stopped.')
    initialization = '''
  const config = input.config;
  if (!config || typeof config !== 'object' || Array.isArray(config))
    throw new Error('config must be a JSON object.');
''' + '\n'.join(declarations)
    if 'MODEL' in fields:
        initialization += '''
  const extraBody = config.extraBody === undefined ? {} : config.extraBody;
  if (!extraBody || typeof extraBody !== 'object' || Array.isArray(extraBody))
    throw new Error('extraBody must be a JSON object.');
  if (Object.hasOwn(extraBody, 'messages')) throw new Error('extraBody cannot override messages.');'''
        if script.count('enable_thinking: ENABLE_THINKING') != 1:
            raise ValueError('Unexpected model request body; migration stopped.')
        script = script.replace('enable_thinking: ENABLE_THINKING', 'enable_thinking: ENABLE_THINKING,\n      ...extraBody')
    if 'AK' in fields:
        initialization += """
  const extraBody = config.extraBody === undefined ? {} : config.extraBody;
  if (!extraBody || typeof extraBody !== 'object' || Array.isArray(extraBody))
    throw new Error('extraBody must be a JSON object.');
  if (Object.hasOwn(extraBody, 'image')) throw new Error('extraBody cannot override image.');"""
        script = script.replace("  const response = await fetch(ocrUrl, {", """  for (const [key, value] of Object.entries(extraBody))
    body.set(key, typeof value === 'string' ? value : JSON.stringify(value));
  const response = await fetch(ocrUrl, {""")
        script = script.replace('await fetch(tokenUrl)', "await fetch(tokenUrl).catch(() => { throw new Error('OCR authentication request failed. Check configuration and network.'); })")
        script = script.replace('await tokenResponse.json()', "await tokenResponse.json().catch(() => { throw new Error('OCR authentication returned invalid JSON.'); })")
    script = script.replace(anchor, anchor + initialization)
    script = script.replace(': ${await response.text()}', '').replace(': ${await tokenResponse.text()}', '')
    script = script.replace('  });\n  if (!response.ok)',
        "  }).catch(() => { throw new Error('Service request failed. Check the service configuration and network.'); });\n  if (!response.ok)")
    script = script.replace('await response.json()',
        "await response.json().catch(() => { throw new Error('Service returned invalid JSON.'); })")
    if 'AK' in fields:
        script = script.replace('  const text = Array.isArray(data.words_result)', "  if (data.error_code) throw new Error('Baidu OCR request failed. Check credentials and OCR parameters.');\n  const text = Array.isArray(data.words_result)")
    return MARKER + script.lstrip(), config


def prepare(root, kind="translation"):
    changes = []
    for folder, fields in (OCR_SERVICES if kind == "ocr" else SERVICES).items():
        directory = root / folder
        source = (directory / 'index.js').read_text(encoding='utf-8-sig')
        config_path = directory / 'config.json'
        existing = None
        if config_path.exists():
            try:
                existing = json.loads(config_path.read_text(encoding='utf-8-sig'))
            except ValueError:
                raise ValueError('Existing config.json is invalid; migration stopped.') from None
            if not isinstance(existing, dict):
                raise ValueError('Existing config.json must be an object; migration stopped.')
        if source.startswith(MARKER):
            if existing is None:
                raise ValueError('Migrated plugin has no config.json; restore it from backup.')
            continue
        updated, config = transform(source, fields)
        manifest = json.loads((directory / 'plugin.json').read_text(encoding='utf-8-sig'))
        manifest.pop('configSchema', None)
        changes.append((directory, updated, config if existing is None else None, manifest))
    return changes


def migrate(root, backup_root, dry_run=False, kind="translation"):
    root = root.resolve()
    backup_root = backup_root.resolve()
    if backup_root.is_relative_to(root.parent):
        raise ValueError('Backup directory must be outside the plugins tree.')
    changes = prepare(root, kind)
    # Check all generated scripts before changing any source file.
    with tempfile.TemporaryDirectory(prefix='rabtrans-migration-check-') as temporary:
        for index, (_, script, _, _) in enumerate(changes):
            check = Path(temporary) / f'{index}.js'
            check.write_text(script, encoding='utf-8')
            result = subprocess.run(['node', '--check', str(check)], capture_output=True)
            if result.returncode:
                raise ValueError('Generated plugin syntax check failed; migration stopped.')
    if dry_run or not changes:
        return len(changes), None
    backup = backup_root / datetime.now().strftime('%Y%m%d-%H%M%S-%f')
    backup.mkdir(parents=True)
    for directory, _, _, _ in changes:
        shutil.copytree(directory, backup / directory.name)
    try:
        for directory, script, config, manifest in changes:
            if config is not None:
                with (directory / 'config.json').open('x', encoding='utf-8') as output:
                    json.dump(config, output, ensure_ascii=False, indent=2)
                    output.write('\n')
            (directory / 'index.js').write_text(script, encoding='utf-8')
            (directory / 'plugin.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    except Exception:
        for directory, _, _, _ in changes:
            shutil.copytree(backup / directory.name, directory, dirs_exist_ok=True)
            if not (backup / directory.name / 'config.json').exists():
                (directory / 'config.json').unlink(missing_ok=True)
        raise
    return len(changes), backup


def migrate_paddle_environment(root, dry_run=False):
    values = {key: os.environ[name] for key, name in {
        'lang': 'RABTRANS_PADDLEOCR_LANG', 'engine': 'RABTRANS_PADDLEOCR_ENGINE',
        'device': 'RABTRANS_PADDLEOCR_DEVICE'}.items() if os.environ.get(name)}
    path = root / 'paddleocr_local' / 'config.json'
    if values and not path.exists() and not dry_run:
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open('x', encoding='utf-8') as output:
            json.dump({'lang': 'ch', **values}, output, indent=2)
            output.write('\n')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--backup-root', type=Path, required=True)
    parser.add_argument('--dry-run', action='store_true')
    parser.add_argument('--kind', choices=['translation', 'ocr'], default='translation')
    args = parser.parse_args()
    try:
        count, backup = migrate(args.root, args.backup_root, args.dry_run, args.kind)
        print(f'{count} plugins {"ready to migrate" if args.dry_run else "migrated"}.')
        if backup:
            print(f'Backup: {backup}')
        if args.kind == 'ocr':
            migrate_paddle_environment(args.root, args.dry_run)
    except Exception:
        # Exceptions from parsers or file operations must not expose local configuration.
        raise SystemExit('Migration failed. No credentials were printed; check plugin files and backup location.') from None
