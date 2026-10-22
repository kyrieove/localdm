# VoxCPM 本地音色接入：实测记录

> 2026-10-22 状态更新：用户决定卸载并移除 VoxCPM，下面保留为历史记录，不是当前安装指引。第 4.4 节的单一根因结论证据不足：访问冲突、CUDA OOM 与当时内存压力不能证明全部失败都由系统提交额度造成；没有释放资源后的成功对照或加载峰值记录。本机未完成成功朗读验收。

日期 2026-10-22。范围：在本机（`D:\2\dev\dm-voxcpm`，分支 `agent/voxcpm`）核实 VoxCPM 能不能接、怎么接。

本文件只记录**实际跑出来的结果**。没跑到的写「未执行」，不拿「代码编译通过」冒充「声音能读出来」。

## 1. 环境事实

| 项 | 实测值 | 取证方式 |
|---|---|---|
| GPU | NVIDIA GeForce RTX 4050 Laptop GPU，显存 **6141 MiB** | `nvidia-smi --query-gpu=name,memory.total` |
| 物理内存 | **15.24 GB** | `Win32_OperatingSystem.TotalVisibleMemorySize` |
| 页面文件 | `C:\pagefile.sys`，**固定 32 GB**（`InitialSize` 与 `MaximumSize` 同为 32768） | `Win32_PageFileSetting` |
| 自动管理页面文件 | **False** | `Win32_ComputerSystem.AutomaticManagedPagefile` |
| 提交上限 | 47.24 GB | `TotalVirtualMemorySize` |
| 已提交 | **39.78 GB**，剩余 7.46 GB | `TotalVirtualMemorySize - FreeVirtualMemorySize` |
| 页面文件已用 | 16.07 GB | `Win32_PageFileUsage.CurrentUsage` |
| Python | 3.10.11 | `python --version` |
| torch | 2.6.0+cu124，`torch.cuda.is_available()` 为 True | 实装后实测 |
| 磁盘余量 | C: 90.5 GB / D: 134.7 GB / E: 118.3 GB | `Win32_LogicalDisk` |

## 2. 依赖安装（已执行，通过）

`pip install voxcpm` 的直接依赖在 Windows 上的 wheel 情况（`pypi.org/pypi/<pkg>/json` 实查）：

| 包 | Windows wheel | 结论 |
|---|---|---|
| `pynini` | **0 个（全版本）** | 装不上 |
| `wetext` | **0 个（全版本）** | 装不上（依赖 kaldifst 与 contractions） |
| `contractions` | 0 个 | 纯 python，可从 sdist 装 |
| `voxcpm` 本身 | 0 个，但是纯 python wheel | 正常安装 |
| `torchcodec` | 有（cp311/cp312/cp313/cp314 win_amd64） | 正常 |
| `kaldifst` | 21 个 | 正常 |
| `onnxruntime` / `soundfile` / `simplejson` | 有 | 正常 |
| `librosa` / `datasets` / `gradio` | 0 个，均为纯 python | 从 sdist 装 |

**实际安装命令与结果**（全部通过）：

```
pip install torch torchaudio --index-url https://download.pytorch.org/whl/cu124
  → Successfully installed torch-2.6.0+cu124 torchaudio-2.6.0+cu124
pip install transformers einops pydantic safetensors librosa soundfile numpy huggingface-hub tqdm
  → Successfully installed（另含 transformers-5.20.2、librosa-0.11.0 等）
pip install --no-deps voxcpm
  → Successfully installed voxcpm-2.0.3
python -c "import voxcpm"  → 成功
```

结论：**`--no-deps` + 手工装推理链在 Windows 上可行**。`wetext`/`pynini` 只服务于文本正则化与 WebUI，推理主路径用不到。README 的 `pip install voxcpm` 在 Windows 上照抄会失败，安装说明必须按上面这条写。

## 3. 模型档位体积（已核实）

HuggingFace API 实查 `safetensors.total`：

| 模型 | 权重体积 | 其它 |
|---|---|---|
| `openbmb/VoxCPM2` | **2.13 GB** | 48kHz，2B，30 语种，支持音色设计与隔離参考克隆 |
| `openbmb/VoxCPM1.5` | **0.75 GB** | 44.1kHz，不支持 `reference_wav_path` |
| `openbmb/VoxCPM-0.5B` | 0（只有 `pytorch_model.bin`） | 16kHz |

## 4. 推理实测（已执行，全部失败）

### 4.1 cuda + VoxCPM2

```
[probe] import ok, torch=2.6.0+cu124, cuda=True
[probe] gpu total=6.4GB free=5.3GB
Fetching 9 files: 100%|██████████| 9/9 [01:04<00:00]
voxcpm_model_path: ...\models--openbmb--VoxCPM2\snapshots\32279ef...
Loading AudioVAE from pytorch: ...\audiovae.pth
Running on device: cuda, dtype: bfloat16
EXIT=-1073741819
```

权重下载成功（2.13 GB，64 秒），加载到 `Running on device: cuda` 后进程以 `0xC0000005`（访问冲突）崩溃。

### 4.2 cpu + VoxCPM2

```
[p] torch=2.6.0+cu124 device=cpu model=openbmb/VoxCPM2
EXIT_A=-1073741819
```

同样崩溃。**CPU 也崩说明问题不在显存。**

### 4.3 cuda + VoxCPM-0.5B（最小档）

完整回溯：

```
File "voxcpm/model/voxcpm.py", line 931, in from_local
    return model.to(model.device).eval().optimize(disable=not optimize)
torch.OutOfMemoryError: CUDA out of memory. Tried to allocate 128.00 MiB.
GPU 0 has a total capacity of 6.00 GiB of which 3.56 GiB is free.
Of the allocated memory 1.36 GiB is allocated by PyTorch
```

**这条报错是定性的关键**：显存空闲 3.56 GB，却申请 128 MB 失败。

### 4.4 根因判定

不是显存不够，是**系统提交额度耗尽**：

- 显卡此刻显存占用只有 693 MiB（`nvidia-smi` 实测），**显存是空的**。
- 提交额度只剩 7.46 GB，而 VoxCPM 加载 2.13 GB 权重 + AudioVAE + CUDA 上下文的峰值远超这个数。
- 页面文件固定 32 GB 且已被占 16.07 GB，所以先撞 `os error 1455`（页面文件太小），再以访问冲突形式崩掉。
- WDDM 下显存分配要向系统提交额度要账，所以报的是 CUDA OOM，实际缺的是提交额度。

提交额度的占用者（`PrivateMemorySize` 实测）：

| 进程 | 私有提交 |
|---|---|
| `qemu-system-x86_64-headless` | 4.46 GB |
| `pythonw` | 2.34 GB |
| `claude` × 22 个 | 合计约 16 GB |
| **全进程合计** | **34.29 GB** |

**这是本机环境问题，不是 VoxCPM 或本软件的缺陷。**

### 4.5 对设计的影响

1. **模型档位必须可配置**，不能写死 VoxCPM2。换一台显存或内存更充裕的机器、或先关掉几个占内存的程序，档位就该能换。因此 `VoxCpmModelPath` 是设置项。
2. **本机当前无法完成端到端朗读验收。** 这一点必须如实标注，不能用「编译通过」「单元测试通过」冒充「声音读出来了」。

## 5. 引擎运行时行为（已执行，通过）

`.local/voxcpm-probe/probe3.py` 实测：

```
=== 1. python 侧依赖探测 ===
  voxcpm 可导入: C:\Users\ASUS\AppData\Roaming\Python\Python310\site-packages\voxcpm\__init__.py
  torch=2.6.0+cu124 cuda=True
=== 2. worker 握手（用假模型目录验证失败路径）===
  worker 存在: True
  退出码: 1
  stdout 首行: {"ok": false, "error": "模型加载失败：Repo id must use alphanumeric chars, ..."}
  是合法 JSON: True, ok=False
```

确认了三点：

1. 依赖探测在装有 voxcpm 的机器上返回可用。
2. 模型缺失时 worker 回**一行合法 JSON**（`ok=false` + 干净的诊断文本），不是崩溃、不是 traceback。
   这正是 `.NET` 侧 `TrySpeakAsync` 判定失败并降级所依赖的信号。
3. worker 的用法校验（缺模型目录参数）退出码为 2，符合脚本约定。

worker 语法检查用 `ast.parse` 通过。

## 6. 上游 API 核实（已读源码，非凭 README）
核对 `src/voxcpm/core.py` 与 `src/voxcpm/model/voxcpm2.py` 确认：

- `VoxCPM.from_pretrained(hf_model_id, load_denoiser=True, optimize=True, device=None, cache_dir=None, local_files_only=False)`；`hf_model_id` 可为本地目录或 HF 仓库名，内部经 `resolve_model_path` 处理。
- `model.generate(text, prompt_wav_path, prompt_text, reference_wav_path, cfg_value=2.0, inference_timesteps=10, seed=...)` 返回 `np.ndarray`。
- **音色设计**的写法是把描述放在正文开头的圆括号内：`text="(沉稳的中年男声)各位晚上好"`。
- `reference_wav_path` **仅 VoxCPM2 支持**；`core.py` 对非 v2 模型显式抛错。
- `prompt_wav_path` 与 `prompt_text` 必须同时提供或同时为 None。
- 采样率取 `model.tts_model.sample_rate`。

## 7. 本次实现（代码层面，已完成）

| 项 | 状态 |
|---|---|
| `TtsVoice` 增加 `Engine` 字段、`TtsEngineKind` 枚举 | 已完成 |
| `IEdgeTtsEngine` 泛化为 `ITtsEngine`，新增 `EdgeTtsVoiceEngine` | 已完成 |
| `TtsVoiceId`：VoxCPM 标识的生成与解析 | 已完成 |
| `VoxCpmVoices`：音色对象与展示名 | 已完成 |
| `VoxCpmOptions`：引擎配置快照 | 已完成 |
| `TtsService` 改为按 `voice.Engine` 分派，三级降级链 | 已完成 |
| `VoxCpmEngine` 与 `voxcpm_worker.py` | 见交付报告 |
| 设置项与设置页接入 | 已完成 |
| 单元测试（分派、降级链、标识解析） | 已完成，不依赖本机 python |

降级链固定为：**所选引擎 → Edge 神经音色 → Windows 内置音色**。任何一级失败都不会丢掉口播文字。

## 8. 未执行与受阻

| 项 | 状态 | 原因 |
|---|---|---|
| VoxCPM2 端到端朗读出声音 | **受阻** | 本机提交额度耗尽（见 4.4），需先释放内存或扩大页面文件 |
| 音色设计实际音质试听 | **受阻** | 同上 |
| 音色克隆实际音质试听 | **受阻** | 同上，且需要参考音频素材 |
| 真实剧本整局朗读验收 | **未执行** | 需要真实 4–6 人整局，属最终验收阶段 |
| 显卡加速的 RTF 指标 | **未测得** | 未跑通一次完整推理，无有效数字可报 |
| CPU 推理的 RTF 指标 | **未测得** | 同上 |

**不要**把以上受阻项写成已通过。等提交额度释放后，重跑 `.local/voxcpm-probe/probe2.py` 即可拿到 RTF 与显存占用。
