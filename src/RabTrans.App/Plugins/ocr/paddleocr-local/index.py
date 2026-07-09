import base64
import contextlib
import importlib.util
import json
import os
import sys
import tempfile
import warnings


def main():
    request = read_input()
    image_base64 = request.get("imageBase64") or ""

    if not image_base64:
        raise RuntimeError("PaddleOCR input did not contain imageBase64.")

    engine = normalize_engine(
        os.environ.get("RABTRANS_PADDLEOCR_ENGINE", "")
    )

    configure_paddle_runtime(engine)
    ensure_engine_dependency(engine or "paddle_static")

    try:
        from paddleocr import PaddleOCR
    except Exception as error:
        raise RuntimeError(
            "Python package 'paddleocr' is not available. "
            "Install it in the Python environment used by RabTrans."
        ) from error

    image_bytes = decode_image_base64(image_base64)

    fd, image_path = tempfile.mkstemp(
        prefix="rabtrans-paddleocr-",
        suffix=".png",
    )
    os.close(fd)

    try:
        with open(image_path, "wb") as image_file:
            image_file.write(image_bytes)

        lang = (
            os.environ.get("RABTRANS_PADDLEOCR_LANG", "ch").strip()
            or "ch"
        )

        with contextlib.redirect_stdout(sys.stderr):
            ocr = create_ocr(
                PaddleOCR,
                lang,
                engine,
            )
            results = run_ocr(
                ocr,
                image_path,
            )

        raw = serialize_results(results)
        lines = collect_text_lines(raw)

        output = {
            "text": "\n".join(lines),
            "raw": raw,
        }

        sys.stdout.write(
            json.dumps(
                output,
                ensure_ascii=False,
            )
        )
        sys.stdout.flush()
    finally:
        try:
            os.remove(image_path)
        except OSError:
            pass


def normalize_engine(engine):
    normalized = (engine or "").strip().lower()

    aliases = {
        "ort": "onnxruntime",
        "paddlepaddle": "paddle",
    }

    return aliases.get(normalized, normalized) or None


def configure_paddle_runtime(engine):
    if engine in (
        None,
        "paddle",
        "paddle_static",
        "paddle_dynamic",
    ):
        os.environ["FLAGS_enable_pir_api"] = "0"
        os.environ["FLAGS_use_mkldnn"] = "0"


def ensure_engine_dependency(engine):
    if engine == "onnxruntime":
        require_module(
            "onnxruntime",
            "Python package 'onnxruntime' is not available. "
            "Install it in the Python environment used by RabTrans.",
        )
        return

    if engine == "transformers":
        require_module(
            "transformers",
            "Python package 'transformers' is not available. "
            "Install it in the Python environment used by RabTrans.",
        )
        return

    if engine in (
        "paddle",
        "paddle_static",
        "paddle_dynamic",
    ):
        require_module(
            "paddle",
            "Python package 'paddlepaddle' is not available. "
            "Install it in the Python environment used by RabTrans.",
        )


def require_module(module_name, error_message):
    if importlib.util.find_spec(module_name) is None:
        raise RuntimeError(error_message)


def create_ocr(paddle_ocr_class, lang, engine):
    kwargs = {
        "lang": lang,
        "use_doc_orientation_classify": False,
        "use_doc_unwarping": False,
        "use_textline_orientation": True,
        "enable_mkldnn": False,
        "device": os.environ.get(
            "RABTRANS_PADDLEOCR_DEVICE",
            "cpu",
        ),
    }

    if engine:
        kwargs["engine"] = engine

    with warnings.catch_warnings():
        warnings.simplefilter(
            "ignore",
            category=DeprecationWarning,
        )

        try:
            return paddle_ocr_class(**kwargs)
        except TypeError:
            kwargs.pop("engine", None)
            return paddle_ocr_class(**kwargs)


def run_ocr(ocr, image_path):
    predict = getattr(ocr, "predict", None)

    if callable(predict):
        result = predict(image_path)
        return list(result) if result is not None else []

    legacy_ocr = getattr(ocr, "ocr", None)

    if callable(legacy_ocr):
        try:
            return legacy_ocr(
                image_path,
                cls=True,
            )
        except TypeError:
            return legacy_ocr(image_path)

    raise RuntimeError(
        "Unsupported PaddleOCR object: missing predict/ocr method."
    )


def serialize_results(results):
    serialized = []

    for result in results:
        data = getattr(result, "json", None)

        if callable(data):
            data = data()

        if isinstance(data, str):
            try:
                data = json.loads(data)
            except json.JSONDecodeError:
                data = None

        if data is None and type(result) is dict:
            data = result

        if data is None:
            save_to_json = getattr(
                result,
                "save_to_json",
                None,
            )

            if callable(save_to_json):
                data = serialize_result_via_file(result)

        if data is None:
            data = result

        serialized.append(
            make_json_safe(data)
        )

    return serialized


def serialize_result_via_file(result):
    fd, json_path = tempfile.mkstemp(
        prefix="rabtrans-paddleocr-result-",
        suffix=".json",
    )
    os.close(fd)

    try:
        try:
            result.save_to_json(
                save_path=json_path,
            )
        except TypeError:
            result.save_to_json(json_path)

        with open(
            json_path,
            "r",
            encoding="utf-8",
        ) as json_file:
            return json.load(json_file)
    finally:
        try:
            os.remove(json_path)
        except OSError:
            pass


def make_json_safe(value):
    if value is None or isinstance(
        value,
        (str, int, float, bool),
    ):
        return value

    if isinstance(value, dict):
        result = {}

        for key, item in value.items():
            try:
                result[str(key)] = make_json_safe(item)
            except Exception:
                continue

        return result

    if isinstance(value, (list, tuple, set)):
        result = []

        for item in value:
            try:
                result.append(
                    make_json_safe(item)
                )
            except Exception:
                continue

        return result

    if isinstance(value, os.PathLike):
        return os.fspath(value)

    tolist = getattr(value, "tolist", None)

    if callable(tolist):
        try:
            return make_json_safe(tolist())
        except Exception:
            pass

    item_method = getattr(value, "item", None)

    if callable(item_method):
        try:
            return make_json_safe(
                item_method()
            )
        except Exception:
            pass

    if value.__class__.__name__ in (
        "Font",
        "Image",
        "FreeTypeFont",
    ):
        return None

    try:
        json.dumps(value)
        return value
    except Exception:
        return str(value)


def collect_text_lines(value):
    lines = []
    collect_text(value, lines)

    normalized = []

    for line in lines:
        text = str(line).strip()

        if text and (
            not normalized
            or normalized[-1] != text
        ):
            normalized.append(text)

    return normalized


def collect_text(value, lines):
    if value is None:
        return

    if isinstance(value, dict):
        for key in (
            "rec_texts",
            "texts",
        ):
            items = value.get(key)

            if isinstance(items, list):
                for item in items:
                    if isinstance(item, str):
                        lines.append(item)

                return

        text = value.get("text")

        if isinstance(text, str):
            lines.append(text)
            return

        for item in value.values():
            collect_text(item, lines)

        return

    if isinstance(value, (list, tuple)):
        if (
            len(value) >= 2
            and isinstance(
                value[1],
                (list, tuple),
            )
            and len(value[1]) >= 1
            and isinstance(
                value[1][0],
                str,
            )
        ):
            lines.append(value[1][0])
            return

        for item in value:
            collect_text(item, lines)


def decode_image_base64(value):
    payload = value.strip()

    if payload.startswith("data:") and "," in payload:
        payload = payload.split(",", 1)[1]

    payload = "".join(payload.split())

    try:
        decoded = base64.b64decode(
            payload,
            validate=True,
        )
    except Exception as error:
        raise RuntimeError(
            "PaddleOCR input imageBase64 is not valid Base64 data."
        ) from error

    if not decoded:
        raise RuntimeError(
            "PaddleOCR input imageBase64 decoded to an empty image."
        )

    return decoded


def read_input():
    raw = sys.stdin.buffer.read().decode(
        "utf-8-sig"
    )
    request = json.loads(raw or "{}")

    if not isinstance(request, dict):
        raise RuntimeError(
            "PaddleOCR input must be a JSON object."
        )

    return request


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(
            str(error),
            file=sys.stderr,
        )
        sys.exit(1)