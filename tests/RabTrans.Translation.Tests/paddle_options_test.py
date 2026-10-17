import importlib.util
import io
import json
from pathlib import Path
import sys
import types
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('paddle_plugin', Path(__file__).resolve().parents[2] / 'src/RabTrans.App/Plugins/ocr/paddleocr-local/index.py')
plugin = importlib.util.module_from_spec(spec)
spec.loader.exec_module(plugin)


class PaddleConfigTests(unittest.TestCase):
    def test_defaults_ignore_old_environment(self):
        with patch.dict('os.environ', {'RABTRANS_PADDLEOCR_LANG': 'old', 'RABTRANS_PADDLEOCR_DEVICE': 'old'}):
            options = plugin.resolve_options({})
            self.assertEqual(options['lang'], 'ch')
            self.assertEqual(options['device'], 'cpu')
            self.assertNotIn('engine', options)

    def test_extra_options_and_effective_engine(self):
        extra = {'vendor': {'list': [False, 0, '', None]}, 'device': 'gpu:0', 'engine': 'ort'}
        options = plugin.resolve_options({'lang': 'en', 'device': 'cpu', 'options': extra})
        self.assertEqual(options['vendor'], extra['vendor'])
        self.assertEqual(options['device'], 'gpu:0')
        self.assertEqual(options['engine'], 'onnxruntime')
        self.assertEqual(options['lang'], 'en')

    def test_invalid_options_and_value_free_initialization_error(self):
        for config in [None, [], {'options': []}, {'lang': False}, {'engine': 3}]:
            with self.assertRaises(RuntimeError):
                plugin.resolve_options(config)
        def failed(**kwargs):
            raise ValueError('private-value')
        with self.assertRaises(RuntimeError) as caught:
            plugin.create_ocr(failed, {'vendor': 'private-value'})
        self.assertNotIn('private-value', str(caught.exception))

    def test_input_config_reaches_constructor_and_image_is_cleaned_up(self):
        captured = {}
        class FakeOCR:
            def __init__(self, **kwargs):
                captured.update(kwargs)
            def predict(self, image):
                captured['image'] = image
                self.assertion = Path(image).read_bytes() == b'fake image'
                if not self.assertion:
                    raise AssertionError('image bytes changed')
                return [{'rec_texts': ['recognized']}]
        module = types.ModuleType('paddleocr')
        module.PaddleOCR = FakeOCR
        request = {'imageBase64': 'ZmFrZSBpbWFnZQ==', 'config': {'lang': 'en', 'options': {'vendor': [False, 0, None]}}}
        output = io.StringIO()
        with patch.object(plugin, 'read_input', return_value=request), patch.object(plugin, 'ensure_engine_dependency'), patch.object(plugin, 'configure_paddle_runtime'), patch.dict(sys.modules, {'paddleocr': module}), patch('sys.stdout', output):
            plugin.main()
        self.assertEqual(json.loads(output.getvalue())['text'], 'recognized')
        self.assertEqual(captured['vendor'], [False, 0, None])
        self.assertEqual(captured['lang'], 'en')
        self.assertFalse(Path(captured['image']).exists())


if __name__ == '__main__':
    unittest.main()
