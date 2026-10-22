"""Edge-TTS 合成与播放。

用法：python edge_speak.py <voice>，待朗读文本从 stdin 读取（UTF-8）。

文本走 stdin 而不是命令行：命令行有长度上限，而且任务管理器能直接看到参数，
公开屏上的口播内容不应当出现在进程列表里。
"""

import ctypes
import os
import sys
import asyncio
import io
import tempfile
import time


def main():
    if len(sys.argv) < 2:
        sys.stderr.write("usage: edge_speak.py <voice>  # 文本从 stdin 读取\n")
        sys.exit(2)

    voice = sys.argv[1]
    text = sys.stdin.buffer.read().decode("utf-8", errors="replace")

    if not text.strip():
        sys.exit(0)

    import edge_tts
    import soundfile as sf
    # WinMM MCI can play longer wave files than PlaySound's documented ~100 KB limit.
    # Stop still works by the parent killing this process while the synchronous play waits.

    def cleanup_stale_wavs():
        cutoff = time.time() - 24 * 60 * 60
        temp_dir = tempfile.gettempdir()
        for name in os.listdir(temp_dir):
            if not name.startswith("localdm-edge-") or not name.endswith(".wav"):
                continue
            path = os.path.join(temp_dir, name)
            try:
                if os.path.isfile(path) and os.path.getmtime(path) < cutoff:
                    os.remove(path)
            except OSError:
                pass

    def play_wav(data, sample_rate):
        path = None
        alias = f"localdm_{os.getpid()}"
        winmm = None
        opened = False
        try:
            handle, path = tempfile.mkstemp(prefix="localdm-edge-", suffix=".wav")
            os.close(handle)
            sf.write(path, data, sample_rate, format="WAV", subtype="PCM_16")

            winmm = ctypes.WinDLL("winmm")
            send = winmm.mciSendStringW
            send.argtypes = [ctypes.c_wchar_p, ctypes.c_wchar_p, ctypes.c_uint, ctypes.c_void_p]
            send.restype = ctypes.c_uint

            result = send(f'open "{path}" type waveaudio alias {alias}', None, 0, None)
            if result:
                raise RuntimeError(f"MCI open failed ({result})")
            opened = True

            result = send(f"play {alias} wait", None, 0, None)
            if result:
                raise RuntimeError(f"MCI playback failed ({result})")
        finally:
            if opened and winmm is not None:
                try:
                    winmm.mciSendStringW(f"close {alias}", None, 0, None)
                except Exception:
                    pass
            if path is not None:
                try:
                    os.remove(path)
                except OSError:
                    pass

    async def run():
        comm = edge_tts.Communicate(text, voice)
        buf = bytearray()
        async for chunk in comm.stream():
            if chunk["type"] == "audio":
                buf.extend(chunk["data"])

        if not buf:
            sys.exit(1)

        # float32 而不是默认的 float64：整段旁白的峰值内存直接减半。
        data, fs = sf.read(io.BytesIO(buf), dtype="float32")
        play_wav(data, fs)

    try:
        cleanup_stale_wavs()
        asyncio.run(run())
    except Exception as e:  # noqa: BLE001 - 任何失败都要让上层知道并降级
        sys.stderr.write(str(e))
        sys.exit(1)


if __name__ == "__main__":
    main()
