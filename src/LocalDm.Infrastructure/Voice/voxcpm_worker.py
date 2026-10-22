"""VoxCPM 常驻合成进程。

用法：python voxcpm_worker.py <模型目录或仓库名>

模型加载一次，之后常驻；每次请求从 stdin 读一行 JSON，合成并播放后
往 stdout 写一行 JSON 结果。

为什么是常驻进程：VoxCPM 是 2B 参数模型，加载权重要几十秒。
每句起一个进程等于每句话都卡半分钟。

协议（每行一个 JSON 对象）：
  请求  {"text": "...", "design": "..." 或 null, "reference_wav": "..." 或 null}
  停止  {"cmd": "stop"}
  响应  {"ok": true} 或 {"ok": false, "error": "..."}

stdout 只走协议。模型加载日志必须走 stderr —— 上游代码会往 stderr 打印，
混进 stdout 会破坏 JSON 行协议。
"""

import json
import os
import sys
import threading

# 播放被要求停止时置位。生成过程无法中断，但播放可以。
_stop_event = threading.Event()


def _log(message):
    print(message, file=sys.stderr, flush=True)


def _load_model(model_path):
    """加载模型。

    device 交给上游自动选：有 CUDA 用 CUDA，没有就用 CPU。
    optimize 关掉：它要 torch.compile 与 triton，Windows 上没有 triton，
    而且首次编译耗时很长，对一个每句只跑一次的进程不划算。
    """
    from voxcpm import VoxCPM

    _log(f"[worker] 正在加载模型：{model_path}")
    model = VoxCPM.from_pretrained(
        model_path,
        load_denoiser=False,
        optimize=False,
        device=None,
    )
    _log("[worker] 模型加载完成")
    return model


def _play(wav, sample_rate):
    """播放音频。返回 True 表示播完了，False 表示中途被要求停止。"""
    import sounddevice as sd

    try:
        sd.play(wav, sample_rate)
    except Exception as exc:  # noqa: BLE001 - 播放失败要让上层知道
        _log(f"[worker] 播放失败：{exc}")
        return True

    # 轮询播放状态：sd.wait() 阻塞着就没法响应停止指令。
    while True:
        if _stop_event.is_set():
            try:
                sd.stop()
            except Exception:  # noqa: BLE001 - 停止失败不影响退出
                pass
            _stop_event.clear()
            return False

        try:
            status = sd.get_stream().active
        except Exception:  # noqa: BLE001 - 没有活动流说明已经放完
            return True

        if not status:
            return True

        threading.Event().wait(0.05)


def _synthesize(model, request):
    """按请求合成并播放。返回结果字典。"""
    text = (request.get("text") or "").strip()
    design = request.get("design")
    reference_wav = request.get("reference_wav")

    if not text:
        return {"ok": False, "error": "文本为空"}

    if design:
        # 音色设计：上游把描述放在正文开头的圆括号里。
        target_text = f"({design}){text}"
    else:
        target_text = text

    kwargs = {"text": target_text, "cfg_value": 2.0, "inference_timesteps": 10}

    if reference_wav:
        if not os.path.exists(reference_wav):
            return {"ok": False, "error": f"参考音频不存在：{reference_wav}"}
        kwargs["reference_wav_path"] = reference_wav

    try:
        wav = model.generate(**kwargs)
    except Exception as exc:  # noqa: BLE001 - 任何失败都要让上层知道并降级
        _log(f"[worker] 合成失败：{exc}")
        return {"ok": False, "error": str(exc)}

    sample_rate = model.tts_model.sample_rate
    _play(wav, sample_rate)
    return {"ok": True}


def main():
    if len(sys.argv) < 2:
        sys.stderr.write("usage: voxcpm_worker.py <模型目录或仓库名>\n")
        sys.exit(2)

    model_path = sys.argv[1]

    try:
        model = _load_model(model_path)
    except Exception as exc:  # noqa: BLE001 - 加载失败要回一行错误给上层
        _log(f"[worker] 模型加载失败：{exc}")
        print(json.dumps({"ok": False, "error": f"模型加载失败：{exc}"}, ensure_ascii=False), flush=True)
        sys.exit(1)

    # 握手：告诉上层「模型已就绪，可以发请求了」。
    print(json.dumps({"ok": True, "ready": True}, ensure_ascii=False), flush=True)

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue

        try:
            request = json.loads(line)
        except json.JSONDecodeError:
            print(json.dumps({"ok": False, "error": "请求不是合法 JSON"}, ensure_ascii=False), flush=True)
            continue

        if request.get("cmd") == "stop":
            _stop_event.set()
            print(json.dumps({"ok": True}, ensure_ascii=False), flush=True)
            continue

        result = _synthesize(model, request)
        print(json.dumps(result, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    main()
