import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('migration', Path(__file__).resolve().parents[2] / 'packaging/Migrate-ServiceConfig.py')
migration = importlib.util.module_from_spec(spec)
spec.loader.exec_module(migration)


class MigrationTests(unittest.TestCase):
    def test_migration_backup_existing_config_and_repeat(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            root = base / 'plugins' / 'translation'
            originals = {}
            for folder, fields in migration.SERVICES.items():
                directory = root / folder
                directory.mkdir(parents=True)
                constants = '\n'.join(f'const {key} = ' + ('false;' if key == 'ENABLE_THINKING' else "'fixture-value';") for key in fields)
                script = constants + '''
async function main() {
  const input = await readInput();
  const body = { enable_thinking: ENABLE_THINKING };
}
'''
                (directory / 'index.js').write_text(script)
                originals[folder] = script
                (directory / 'plugin.json').write_text(json.dumps({'id': folder + '_id', 'configSchema': {}}))
            existing = '{"token":"existing","vendor":{"nested":[false,0,null]}}'
            (root / 'caiyun/config.json').write_text(existing)
            count, backup = migration.migrate(root, base / 'backups')
            self.assertEqual(count, 3)
            self.assertEqual((root / 'caiyun/config.json').read_text(), existing)
            for folder in migration.SERVICES:
                self.assertEqual((backup / folder / 'index.js').read_text(), originals[folder])
                script = (root / folder / 'index.js').read_text()
                self.assertNotIn('fixture-value', script)
                self.assertIn('input.config', script)
                self.assertEqual(json.loads((root / folder / 'plugin.json').read_text()), {'id': folder + '_id'})
            self.assertEqual(json.loads((root / 'deepseek/config.json').read_text())['enableThinking'], False)
            self.assertEqual(migration.migrate(root, base / 'backups'), (0, None))

    def test_ocr_migration_and_environment_seed(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / 'plugins' / 'ocr'
            directory = root / 'baidu-basic-ocr'
            directory.mkdir(parents=True)
            source = "const AK = 'fake-ak';\nconst SK = 'fake-sk';\nasync function main() {\n  const input = await readInput();\n}\n"
            (directory / 'index.js').write_text(source)
            (directory / 'plugin.json').write_text('{"id":"baidu_basic_ocr","type":"ocr","configSchema":{}}')
            count, backup = migration.migrate(root, Path(temporary) / 'backups', kind='ocr')
            self.assertEqual(count, 1)
            self.assertEqual((backup / directory.name / 'index.js').read_text(), source)
            self.assertEqual(json.loads((directory / 'config.json').read_text()), {'ak': 'fake-ak', 'sk': 'fake-sk'})
            self.assertEqual(migration.migrate(root, Path(temporary) / 'backups', kind='ocr'), (0, None))
            with patch.dict('os.environ', {'RABTRANS_PADDLEOCR_LANG': 'en', 'RABTRANS_PADDLEOCR_DEVICE': 'gpu:0'}):
                migration.migrate_paddle_environment(root)
                path = root / 'paddleocr_local' / 'config.json'
                first = path.read_text()
                self.assertEqual(json.loads(first)['device'], 'gpu:0')
                migration.migrate_paddle_environment(root)
                self.assertEqual(path.read_text(), first)

    def test_refuses_expressions_without_evaluating_them(self):
        with self.assertRaises(ValueError):
            migration.transform("const API_KEY = dangerous();\n", {'API_KEY': 'apiKey'})


if __name__ == '__main__':
    unittest.main()
