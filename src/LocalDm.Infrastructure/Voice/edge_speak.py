"""Edge-TTS 合成与播放。

用法：python edge_speak.py <voice>，待朗读文本从 stdin 读取（UTF-8）。

文本走 stdin 而不是命令行：命令行有长度上限，而且任务管理器能直接看到参数，
公开屏上的口播内容不应当出现在进程列表里。
"""

import sys
import asyncio
import io


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
    import sounddevice as sd

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
        sd.play(data, fs)
        sd.wait()

    try:
        asyncio.run(run())
    except Exception as e:  # noqa: BLE001 - 任何失败都要让上层知道并降级
        sys.stderr.write(str(e))
        sys.exit(1)


if __name__ == "__main__":
    main()
