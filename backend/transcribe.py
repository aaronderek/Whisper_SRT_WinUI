#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Whisper 字幕转录后端 (faster-whisper / CTranslate2)

被 WinUI 前端作为子进程调用。所有结构化输出通过 **stdout** 以 "每行一个 JSON"
的形式发出（供前端解析进度）；模型下载日志、库告警等走 stderr。

针对 RTX 50 系 (Blackwell / sm_120) 的要点：
  - 默认 compute_type=float16（INT8 在 50 系上会触发 cuBLAS NOT_SUPPORTED 崩溃）。
  - 需要 CTranslate2 >= 4.5 + CUDA 12 + cuDNN 9。
  - Windows 下自动把 pip 安装的 nvidia cuBLAS / cuDNN 的 bin 目录加入 DLL 搜索路径，
    这样无需手动改 PATH 就能用上 GPU。

用法示例：
  python transcribe.py --check
  python transcribe.py --input video.mp4 --output video.srt --model large-v3 --language en
  python transcribe.py --input video.mp4 --output video.txt --format txt --language en

输出格式由 --format 决定：
  srt（默认）逐条写出带时间码的字幕；
  txt        只写不带时间码的纯文本文稿，按句末标点合并成自然段落。
"""

import argparse
import json
import os
import sys
import time
import traceback


# ---------------------------------------------------------------------------
# stdout JSON 协议
# ---------------------------------------------------------------------------
def emit(obj):
    """向前端发送一条 JSON 消息（stdout，单行，立即 flush）。"""
    try:
        sys.stdout.write(json.dumps(obj, ensure_ascii=False) + "\n")
        sys.stdout.flush()
    except Exception:
        # stdout 可能已被关闭（前端取消并 kill），忽略即可
        pass


def log(message):
    """诊断信息走 stderr，不干扰 stdout 的 JSON 协议。"""
    try:
        sys.stderr.write(str(message) + "\n")
        sys.stderr.flush()
    except Exception:
        pass


# ---------------------------------------------------------------------------
# Windows: 让 pip 安装的 CUDA 运行库可被发现
# ---------------------------------------------------------------------------
def prepare_cuda_dll_search_paths():
    if os.name != "nt":
        return
    try:
        import site
        import sysconfig

        roots = set()
        try:
            for p in site.getsitepackages():
                roots.add(p)
        except Exception:
            pass
        try:
            roots.add(site.getusersitepackages())
        except Exception:
            pass
        purelib = sysconfig.get_paths().get("purelib")
        if purelib:
            roots.add(purelib)

        bin_dirs = []
        for sp in roots:
            nvidia_root = os.path.join(sp, "nvidia")
            if not os.path.isdir(nvidia_root):
                continue
            for name in os.listdir(nvidia_root):
                bin_dir = os.path.join(nvidia_root, name, "bin")
                if os.path.isdir(bin_dir):
                    bin_dirs.append(bin_dir)
                    try:
                        os.add_dll_directory(bin_dir)
                    except OSError:
                        pass

        if bin_dirs:
            # add_dll_directory 只影响带搜索标志的 LoadLibraryEx；CTranslate2 的
            # 延迟加载走普通 LoadLibrary（只搜 PATH），所以 PATH 也要加上。
            os.environ["PATH"] = os.pathsep.join(bin_dirs) + os.pathsep + os.environ.get("PATH", "")
            # 再显式预加载关键 DLL，加载失败留给后续真正使用处报错
            import ctypes
            for pattern in ("cublasLt64_12.dll", "cublas64_12.dll", "cudnn64_9.dll"):
                for d in bin_dirs:
                    full = os.path.join(d, pattern)
                    if os.path.isfile(full):
                        try:
                            ctypes.WinDLL(full)
                        except OSError as exc:
                            log(f"[warn] preload {pattern}: {exc}")
                        break
    except Exception as exc:  # noqa: BLE001
        log(f"[warn] prepare_cuda_dll_search_paths failed: {exc}")


# ---------------------------------------------------------------------------
# 模型名映射（前端下拉框 -> faster-whisper 可识别的名称/仓库 id）
# ---------------------------------------------------------------------------
MODEL_MAP = {
    "large-v3": "large-v3",
    # 该模型在 HF 上已由 mobiuslabsgmbh 改名到 dropbox-dash。这里直接写规范仓库 id，
    # 不走旧名的重定向：旧命名空间一旦被他人注册，带旧名的请求就会被接走。
    "large-v3-turbo": "dropbox-dash/faster-whisper-large-v3-turbo",
    "distil-large-v3.5": "deepdml/faster-distil-whisper-large-v3.5",
    "distil-large-v3": "distil-large-v3",
    "medium": "medium",
    "small": "small",
    "base": "base",
    "tiny": "tiny",
}

# 第三方（非 SYSTRAN 官方）模型仓库锁定 revision：上游权重即使被替换也不会静默生效。
# SYSTRAN 官方仓库不在此表内（跟随官方最新版）。升级模型时需同步更新这里。
MODEL_PINS = {
    "dropbox-dash/faster-whisper-large-v3-turbo":
        "0a363e9161cbc7ed1431c9597a8ceaf0c4f78fcf",
    "deepdml/faster-distil-whisper-large-v3.5":
        "3fc5a9a7e601a131caaaa09d91b653ed20643f60",
}


def resolve_model(name):
    return MODEL_MAP.get(name, name)


# 内置模型名 -> HuggingFace 仓库 id 的兜底映射（faster_whisper.utils._MODELS 不可用时）
FALLBACK_REPOS = {
    "large-v3": "Systran/faster-whisper-large-v3",
    "large-v3-turbo": "dropbox-dash/faster-whisper-large-v3-turbo",
    "distil-large-v3": "Systran/faster-distil-whisper-large-v3",
    "medium": "Systran/faster-whisper-medium",
    "small": "Systran/faster-whisper-small",
    "base": "Systran/faster-whisper-base",
    "tiny": "Systran/faster-whisper-tiny",
}


def repo_for(name):
    """把前端模型名解析成 HuggingFace 仓库 id。"""
    resolved = resolve_model(name)
    if "/" in resolved:
        return resolved
    try:
        from faster_whisper.utils import _MODELS

        if resolved in _MODELS:
            return _MODELS[resolved]
    except Exception:
        pass
    return FALLBACK_REPOS.get(resolved, f"Systran/faster-whisper-{resolved}")


def pinned_revision(name):
    """该模型应锁定的 git revision；官方仓库不在锁定表内，返回 None。"""
    return MODEL_PINS.get(repo_for(name))


# ---------------------------------------------------------------------------
# 模型缓存管理 (--models-status / --download-model / --delete-model)
# ---------------------------------------------------------------------------
def do_models_status():
    """列出各模型在 HuggingFace 本地缓存中的状态。"""
    try:
        from huggingface_hub import scan_cache_dir

        cached = {}
        try:
            for repo in scan_cache_dir().repos:
                cached[repo.repo_id] = int(repo.size_on_disk)
        except Exception as exc:  # noqa: BLE001  缓存目录不存在等
            log(f"[warn] scan_cache_dir: {exc}")

        items = []
        for name in MODEL_MAP:
            repo = repo_for(name)
            items.append({
                "name": name,
                "repo": repo,
                "downloaded": repo in cached,
                "size_bytes": cached.get(repo, 0),
            })
        emit({"type": "models", "items": items})
        return 0
    except Exception as exc:  # noqa: BLE001
        emit({"type": "error", "message": f"{type(exc).__name__}: {exc}"})
        return 1


def do_download_model(name):
    repo = repo_for(name)
    revision = pinned_revision(name)
    emit({"type": "status", "stage": "downloading",
          "message": f"下载模型 {name}（{repo}{' @ ' + revision[:8] if revision else ''}）..."})
    try:
        try:
            from faster_whisper.utils import download_model

            download_model(resolve_model(name), revision=revision)
        except ImportError:
            from huggingface_hub import snapshot_download

            snapshot_download(repo, revision=revision)
        emit({"type": "done", "model": name})
        return 0
    except Exception as exc:  # noqa: BLE001
        emit({"type": "error", "message": f"下载失败: {type(exc).__name__}: {exc}"})
        return 1


def do_delete_model(name):
    repo = repo_for(name)
    try:
        from huggingface_hub import scan_cache_dir

        info = scan_cache_dir()
        hashes = [rev.commit_hash
                  for r in info.repos if r.repo_id == repo
                  for rev in r.revisions]
        if not hashes:
            emit({"type": "error", "message": f"本地缓存中没有 {repo}"})
            return 1
        info.delete_revisions(*hashes).execute()
        emit({"type": "done", "model": name})
        return 0
    except Exception as exc:  # noqa: BLE001
        emit({"type": "error", "message": f"删除失败: {type(exc).__name__}: {exc}"})
        return 1


# ---------------------------------------------------------------------------
# SRT 生成
# ---------------------------------------------------------------------------
def format_timestamp(seconds):
    if seconds is None or seconds < 0:
        seconds = 0.0
    total_ms = int(round(float(seconds) * 1000.0))
    hours, total_ms = divmod(total_ms, 3600_000)
    minutes, total_ms = divmod(total_ms, 60_000)
    secs, millis = divmod(total_ms, 1000)
    return f"{hours:02d}:{minutes:02d}:{secs:02d},{millis:03d}"


def wrap_text(text, max_chars):
    """把一句字幕按最大字符数做贪心换行；max_chars<=0 时不换行。"""
    text = " ".join(text.strip().split())
    if max_chars <= 0 or len(text) <= max_chars:
        return text
    words = text.split(" ")
    lines, current = [], ""
    for word in words:
        if not current:
            current = word
        elif len(current) + 1 + len(word) <= max_chars:
            current += " " + word
        else:
            lines.append(current)
            current = word
    if current:
        lines.append(current)
    return "\n".join(lines)


def write_srt_cue(handle, index, start, end, text, max_chars):
    handle.write(f"{index}\n")
    handle.write(f"{format_timestamp(start)} --> {format_timestamp(end)}\n")
    handle.write(wrap_text(text, max_chars) + "\n\n")
    handle.flush()


# ---------------------------------------------------------------------------
# 纯文本文稿生成（--format txt：无时间码，按标点合并成自然段落）
# ---------------------------------------------------------------------------
SENTENCE_END_CHARS = ".!?。！？…‼⁇⁈⁉"
# 判断句末标点前先跳过的闭合引号 / 括号（例如 他说：“好。”）
CLOSING_CHARS = "\"'”’」』）)】》]"
# 段落长度上限：模型长时间不吐标点时的兜底，避免整篇挤成一段
PARAGRAPH_MAX_CHARS = 800


def is_cjk_char(ch):
    """是否 CJK 字符（中/日文）——这类文字之间拼接不加空格。"""
    code = ord(ch)
    return (
        0x3040 <= code <= 0x30FF        # 平假名 / 片假名
        or 0x3400 <= code <= 0x4DBF     # CJK 扩展 A
        or 0x4E00 <= code <= 0x9FFF     # CJK 基本区
        or 0xF900 <= code <= 0xFAFF     # CJK 兼容表意文字
        or 0x20000 <= code <= 0x2FA1F   # CJK 扩展 B 及以后
    )


def join_transcript(prev, nxt):
    """拼接两段文本：CJK 之间直接相连，其余按西文习惯补一个空格。"""
    if not prev:
        return nxt
    if not nxt:
        return prev
    if is_cjk_char(prev[-1]) or is_cjk_char(nxt[0]):
        return prev + nxt
    return prev + " " + nxt


def ends_sentence(text):
    """（忽略结尾引号/括号后）是否以句末标点结尾。"""
    stripped = text.rstrip(CLOSING_CHARS).rstrip()
    return bool(stripped) and stripped[-1] in SENTENCE_END_CHARS


class SrtWriter:
    """逐条写出带时间码的 SRT 字幕。"""

    def __init__(self, handle, max_chars=42):
        self._handle = handle
        self._max_chars = max_chars
        self.units = 0     # 已写出的字幕条数

    def add(self, start, end, text):
        self.units += 1
        write_srt_cue(self._handle, self.units, start, end, text, self._max_chars)

    def close(self):
        pass


class TranscriptWriter:
    """把逐条字幕合并成不带时间码的自然段落，每个段落占一行。"""

    def __init__(self, handle, max_chars=PARAGRAPH_MAX_CHARS):
        self._handle = handle
        self._max_chars = max_chars
        self._buffer = ""
        self.units = 0     # 已写出的段落数

    def add(self, _start, _end, text):
        # 归一化空白：SRT 里的换行/多余空格在文稿中没有意义
        text = " ".join((text or "").split())
        if not text:
            return
        # 长度兜底：模型长时间不吐标点时按上限切断，避免整篇挤成一段
        if self._buffer and len(self._buffer) + 1 + len(text) > self._max_chars:
            self.flush()
        self._buffer = join_transcript(self._buffer, text)
        if ends_sentence(self._buffer):
            self.flush()

    def flush(self):
        if not self._buffer:
            return
        self._handle.write(self._buffer + "\n")
        self._handle.flush()
        self._buffer = ""
        self.units += 1

    def close(self):
        self.flush()


def create_writer(handle, fmt, max_line_chars):
    """按 --format 创建写入器；txt 模式与换行设置无关。"""
    if fmt == "txt":
        return TranscriptWriter(handle)
    return SrtWriter(handle, max_line_chars)


# ---------------------------------------------------------------------------
# 环境自检 (--check)
# ---------------------------------------------------------------------------
def get_gpu_name():
    try:
        import subprocess

        out = subprocess.check_output(
            ["nvidia-smi", "--query-gpu=name", "--format=csv,noheader"],
            text=True,
            timeout=5,
        )
        first = out.strip().splitlines()
        return first[0].strip() if first else None
    except Exception:
        return None


def package_version(pkg):
    try:
        from importlib.metadata import version

        return version(pkg)
    except Exception:
        return None


def do_check():
    result = {
        "type": "check",
        "ok": False,
        "cuda_device_count": 0,
        "gpu_name": None,
        "faster_whisper": package_version("faster-whisper"),
        "ctranslate2": package_version("ctranslate2"),
        "python": sys.version.split()[0],
        "error": None,
    }
    try:
        prepare_cuda_dll_search_paths()
        import ctranslate2  # noqa: WPS433

        result["cuda_device_count"] = int(ctranslate2.get_cuda_device_count())
        result["gpu_name"] = get_gpu_name()
        result["ok"] = True
    except Exception as exc:  # noqa: BLE001
        result["error"] = f"{type(exc).__name__}: {exc}"
    emit(result)
    return 0 if result["ok"] else 2


# ---------------------------------------------------------------------------
# 时长探测（用于进度百分比）
# ---------------------------------------------------------------------------
def probe_duration(path):
    try:
        import av

        with av.open(path) as container:
            if container.duration is not None:
                return float(container.duration) / av.time_base
            for stream in container.streams:
                if stream.duration and stream.time_base:
                    return float(stream.duration * stream.time_base)
    except Exception as exc:  # noqa: BLE001
        log(f"[warn] probe_duration failed: {exc}")
    return None


# ---------------------------------------------------------------------------
# 主转录流程
# ---------------------------------------------------------------------------
def do_transcribe(args):
    prepare_cuda_dll_search_paths()

    emit({"type": "status", "stage": "loading_model",
          "message": f"加载模型 {args.model}（首次使用会自动下载，可能较慢）..."})

    from faster_whisper import WhisperModel  # 延迟导入，便于 --check 更快

    model_id = resolve_model(args.model)
    device = args.device
    compute_type = args.compute_type
    # 第三方仓库锁定 revision：上游权重被替换时不会静默生效（官方仓库为 None）
    revision = pinned_revision(args.model)
    if revision:
        emit({"type": "status", "stage": "loading_model",
              "message": f"模型来源 {repo_for(args.model)} @ {revision[:8]}（已锁定版本）"})

    try:
        model = WhisperModel(model_id, device=device, compute_type=compute_type, revision=revision)
    except Exception as exc:  # noqa: BLE001
        # GPU 初始化失败时，可选地回退到 CPU（int8），避免完全不可用
        if device == "cuda" and args.cpu_fallback:
            log(f"[warn] CUDA 初始化失败，回退 CPU: {exc}")
            emit({"type": "status", "stage": "cpu_fallback",
                  "message": "GPU 初始化失败，已回退到 CPU（速度会明显变慢）。"})
            device, compute_type = "cpu", "int8"
            try:
                model = WhisperModel(model_id, device=device, compute_type=compute_type, revision=revision)
            except Exception as cpu_exc:  # noqa: BLE001
                # CPU 回退也失败时，原始 GPU 错误才是真正可诊断的信息，必须一并带出
                raise RuntimeError(
                    f"CPU 回退也失败: {type(cpu_exc).__name__}: {cpu_exc}；"
                    f"原始 CUDA 错误: {type(exc).__name__}: {exc}"
                ) from cpu_exc
        else:
            raise

    language = None if args.language.lower() in ("auto", "") else args.language
    emit({"type": "status", "stage": "transcribing",
          "message": "开始转录...", "device": device, "compute_type": compute_type})

    segments, info = model.transcribe(
        args.input,
        language=language,
        task=args.task,
        beam_size=args.beam_size,
        vad_filter=(not args.no_vad),
        word_timestamps=args.word_timestamps,
        temperature=0.0,
    )

    duration = getattr(info, "duration", None) or probe_duration(args.input) or 0.0
    emit({
        "type": "info",
        "language": getattr(info, "language", language),
        "language_probability": round(float(getattr(info, "language_probability", 0.0) or 0.0), 4),
        "duration": round(float(duration), 3),
        "device": device,
        "compute_type": compute_type,
    })

    os.makedirs(os.path.dirname(os.path.abspath(args.output)) or ".", exist_ok=True)

    started = time.time()
    count = 0
    last_pct = -1.0
    with open(args.output, "w", encoding="utf-8", newline="\n") as handle:
        writer = create_writer(handle, args.format, args.max_line_chars)
        for segment in segments:
            count += 1
            text = (segment.text or "").strip()
            writer.add(segment.start, segment.end, text)

            if args.emit_segments:
                emit({"type": "segment", "index": count,
                      "start": round(float(segment.start), 3),
                      "end": round(float(segment.end), 3),
                      "text": text})

            if duration > 0:
                pct = min(100.0, float(segment.end) / duration * 100.0)
                if pct - last_pct >= 0.5 or pct >= 100.0:
                    last_pct = pct
                    emit({"type": "progress", "current": round(float(segment.end), 2),
                          "total": round(float(duration), 2), "pct": round(pct, 2)})
            elif count % 10 == 0:
                # 时长未知（部分容器探测不到）：无法给百分比，用已转录位置证明没有卡死
                emit({"type": "status", "stage": "transcribing",
                      "message": f"转录中（时长未知）· 已到 {format_timestamp(segment.end)} · {count} 条"})
        writer.close()
        units = writer.units

    elapsed = time.time() - started
    speed = (duration / elapsed) if elapsed > 0 and duration > 0 else 0.0
    emit({"type": "progress", "current": round(float(duration), 2),
          "total": round(float(duration), 2), "pct": 100.0})
    emit({"type": "done", "output": os.path.abspath(args.output), "segments": count,
          "format": args.format, "paragraphs": units if args.format == "txt" else 0,
          "elapsed": round(elapsed, 2), "speed_x": round(speed, 2)})
    return 0


# ---------------------------------------------------------------------------
# 参数解析 + 入口
# ---------------------------------------------------------------------------
def build_parser():
    parser = argparse.ArgumentParser(description="Whisper 字幕转录后端")
    parser.add_argument("--check", action="store_true", help="仅检测 GPU / 依赖环境")
    parser.add_argument("--models-status", action="store_true", help="列出模型本地缓存状态")
    parser.add_argument("--download-model", metavar="NAME", help="预下载指定模型到本地缓存")
    parser.add_argument("--delete-model", metavar="NAME", help="删除指定模型的本地缓存")
    parser.add_argument("--input", help="输入视频或音频文件路径")
    parser.add_argument("--output", help="输出文件路径（扩展名应与 --format 一致）")
    parser.add_argument("--format", choices=["srt", "txt"], default="srt",
                        help="输出格式：srt 字幕（默认）；txt 纯文本文稿，无时间码、按标点分段")
    parser.add_argument("--model", default="large-v3", help="模型名（默认 large-v3）")
    parser.add_argument("--language", default="en", help="语言代码，或 auto 自动检测（默认 en）")
    parser.add_argument("--task", default="transcribe", choices=["transcribe", "translate"])
    parser.add_argument("--device", default="cuda", choices=["cuda", "cpu", "auto"])
    parser.add_argument("--compute-type", dest="compute_type", default="float16",
                        help="计算精度：float16（50 系推荐）/ bfloat16 / float32 / int8_float16 / int8")
    parser.add_argument("--beam-size", dest="beam_size", type=int, default=5)
    parser.add_argument("--max-line-chars", dest="max_line_chars", type=int, default=42,
                        help="每行最大字符数，用于换行；0 表示不换行")
    parser.add_argument("--no-vad", dest="no_vad", action="store_true", help="关闭 VAD 静音过滤")
    parser.add_argument("--word-timestamps", dest="word_timestamps", action="store_true")
    parser.add_argument("--emit-segments", dest="emit_segments", action="store_true",
                        help="转录时把每条字幕文本回传前端（用于实时预览）")
    parser.add_argument("--cpu-fallback", dest="cpu_fallback", action="store_true",
                        help="GPU 初始化失败时自动回退 CPU")
    return parser


def main(argv=None):
    args = build_parser().parse_args(argv)

    if args.check:
        return do_check()
    if args.models_status:
        return do_models_status()
    if args.download_model:
        return do_download_model(args.download_model)
    if args.delete_model:
        return do_delete_model(args.delete_model)

    if not args.input or not args.output:
        emit({"type": "error", "message": "缺少 --input 或 --output 参数"})
        return 2
    if not os.path.isfile(args.input):
        emit({"type": "error", "message": f"输入文件不存在: {args.input}"})
        return 2

    try:
        return do_transcribe(args)
    except Exception as exc:  # noqa: BLE001
        emit({"type": "error", "message": f"{type(exc).__name__}: {exc}",
              "traceback": traceback.format_exc()})
        return 1


if __name__ == "__main__":
    sys.exit(main())
