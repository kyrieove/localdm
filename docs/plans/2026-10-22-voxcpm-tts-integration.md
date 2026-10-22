# 接入 VoxCPM 本地神经音色

日期 2026-10-22。状态：方案已写，代码未实施，未在本机跑通端到端朗读。

## 1. 要解决的问题

现在的软件只有两条朗读路径（`src/LocalDm.Infrastructure/Voice/`）：

| 路径 | 引擎 | 依赖 | 联网 | 音色质量 |
|---|---|---|---|---|
| 首选 | Edge-TTS | python + edge-tts + sounddevice | 必须（微软服务器） | 好 |
| 降级 | Windows System.Speech | 系统自带 | 不需要 | 机械音 |

Edge-TTS 要求文本出本机到微软服务器。对剧本杀来说，主持人口播的公开文字本身不算秘密，但一次完整的对局会持续把文字送出本机。VoxCPM 提供第三条路：**完全本机的神经音色**。

用户的要求是「把这个声音也接入」。核对上游仓库后确认，VoxCPM 有三种用法（`OpenBMB/VoxCPM`，Apache-2.0）：

| 用法 | 需要什么 | 适合本软件吗 |
|---|---|---|
| Voice Design（音色设计） | 只要一段文字描述 | 是。零配置，用户写「沉稳的中年男声」就有新音色 |
| Voice Cloning（音色克隆） | 一段参考音频 | 是，但要先有参考音频文件 |
| Ultimate Cloning | 参考音频 + 它的逐字稿 | 同上，逐字稿是额外负担 |

本方案接**前两种**：设计音色开箱即用；克隆作为可选增强，参考音频走用户自备文件。

## 2. 上游 API 的实际形状（已核对源码，不是凭 README 转述）

核对 `src/voxcpm/core.py` 与 `src/voxcpm/model/voxcpm2.py` 得到的确定事实：

- `VoxCPM.from_pretrained(hf_model_id, load_denoiser=True, optimize=True, device=None, cache_dir=None, local_files_only=False)`。`hf_model_id` 可以是本地目录，也可以是 HuggingFace 仓库名。
- `model.generate(text=..., prompt_wav_path=..., prompt_text=..., reference_wav_path=..., cfg_value=2.0, inference_timesteps=10, seed=...)` 返回 `np.ndarray` 波形。
- 音色设计的写法是**把描述放在正文开头的圆括号里**：`text="(沉稳的中年男声)各位晚上好"`。
- `reference_wav_path` **只有 VoxCPM2 支持**（`core.py` 里对非 v2 模型显式抛错）。VoxCPM1.5 与 0.5B 不支持隔離参考音频那种克隆。
- `prompt_wav_path` 与 `prompt_text` 必须同时给或同时不给（`core.py` 里 `!=` 判断直接抛 `ValueError`）。
- 采样率从 `model.tts_model.sample_rate` 取（VoxCPM2 是 48kHz，1.5 是 44.1kHz，0.5B 是 16kHz）。
- `from_pretrained` 内部会走 `resolve_model_path`，本地目录直接用，否则 `snapshot_download` 下到 HF 缓存。

## 3. 本机实测到的环境事实

| 事实 | 数值 | 来源 |
|---|---|---|
| GPU | RTX 4050 Laptop，**显存 6141 MiB** | `nvidia-smi` |
| Python | 3.10.11 | `python --version` |
| torch | 2.6.0+cu124，`cuda.is_available()` 为 True | 实装后实测 |
| 磁盘余量 | C: 90.5 GB / D: 134.7 GB | `Win32_LogicalDisk` |
| VoxCPM2 权重 | safetensors 合计 **2.13 GB** | HuggingFace API |
| VoxCPM1.5 权重 | safetensors 合计 **0.75 GB** | HuggingFace API |
| VoxCPM-0.5B 权重 | 无 safetensors，只有 `pytorch_model.bin` | HuggingFace API |

### 3.1 Windows 上装不装得上

`pip install voxcpm` 的直接依赖里，有几个在 Windows 上**全部版本都没有 wheel**：

| 包 | Windows wheel | 后果 |
|---|---|---|
| `pynini` | 0 个（全版本） | `wetext` 的依赖，装不上 |
| `wetext` | 0 个 | 装不上 |
| `contractions` | 0 个 | 纯 python，可从 sdist 装 |
| `voxcpm` 本身 | 0 个，但是纯 python wheel | 正常 |
| `torchcodec` | 有 | 正常 |
| `kaldifst` | 21 个 | 正常 |
| `onnxruntime` / `soundfile` / `simplejson` | 有 | 正常 |
| `librosa` / `datasets` / `gradio` | 0 个，但都是纯 python | 从 sdist 装 |

实测结论：**`pip install --no-deps voxcpm` + 手工装推理链**可以跑通。已验证的最小推理依赖是：

```
torch>=2.5.0  torchaudio>=2.5.0  transformers  einops  pydantic  safetensors
librosa  soundfile  numpy  huggingface-hub  tqdm
```

`wetext`/`pynini` 只在文本正则化（`normalize=True`）与 WebUI 里用得上，推理主路径用不到，所以 `--no-deps` 之后不必补。这也意味着**安装说明要按这条写**，不能照抄 README 的 `pip install voxcpm`。

装完后 `import voxcpm` 成功。

### 3.2 6GB 显存够不够

上游 README 的对照表标 VoxCPM2 需约 **8GB** 显存，本机只有 **6GB**。这是本次接入最大的不确定项，因此**不靠推测，直接实测**。

实测三个档位（`openbmb/VoxCPM2`、`openbmb/VoxCPM1.5`、`openbmb/VoxCPM-0.5B`）全部失败，但**失败原因不是显存**，而是**系统提交额度耗尽**。

### 3.3 实测到的真正阻塞：提交额度耗尽

三个探针的失败输出互相印证，指向同一个根因：

| 探针 | 失败点 | 报错 |
|---|---|---|
| cuda + VoxCPM2 | 模型搬上 GPU 时崩溃 | `0xC0000005`（访问冲突） |
| cpu + VoxCPM2 | 加载权重时崩溃 | `0xC0000005` |
| cuda + VoxCPM-0.5B | `model.to(device)` | `torch.OutOfMemoryError` |

第三次的报错最有信息量，它自相矛盾：

> `GPU 0 has a total capacity of 6.00 GiB of which 3.56 GiB is free. ... Tried to allocate 128.00 MiB.`
> `Of the allocated memory 1.36 GiB is allocated by PyTorch`

**显存空闲 3.56 GB，却分配 128 MB 失败**。这说明请求的并不是显存，而是**系统提交额度**。WDDM 下显存分配要向系统提交额度要账，两者不是一回事。

本机提交额度实测：

| 项 | 数值 |
|---|---|
| 物理内存 | 15.24 GB |
| 页面文件 | **固定 32 GB**（`AutomaticManagedPagefile = False`，非自动管理） |
| 提交上限 | 47.24 GB |
| 已提交 | **39.78 GB** |
| 剩余提交额度 | **7.46 GB** |
| 页面文件当前已用 | 16.07 GB |
| 显卡显存此刻 | 6141 MiB 总量，仅用 693 MiB（**显存是空的**） |

已提交额度被本机其它程序占满：

| 进程 | 私有提交 |
|---|---|
| `qemu-system-x86_64-headless` | 4.46 GB |
| `pythonw` | 2.34 GB |
| `claude` × 22 个 | 合计约 16 GB |
| **全进程私有提交合计** | **34.29 GB** |

VoxCPM2 加载 2.13 GB 权重 + AudioVAE + CUDA 上下文，峰值远超剩余的 7.46 GB，于是先撞上 `os error 1455`（页面文件太小），再以访问冲突的形式崩掉。

**这是本机环境问题，不是 VoxCPM 或本软件的缺陷。** 但它有两个直接后果，决定了方案：

1. **档位必须可配置**。不能写死 VoxCPM2：换一台显存或内存更充裕的机器，或先关掉几个占内存的程序，档位就该能换。所以模型目录是设置项，不是常量。
2. **本机当前无法完成端到端朗读验收**。这一条必须如实写进验收记录，不能拿「代码编译通过」冒充「声音能读出来」。

## 4. 架构决定

### 4.1 引擎模型：常驻进程，不是一句一起一个进程

Edge-TTS 现在的做法是每句起一个 python 进程（`edge_speak.py` 读 stdin、合成、播放、退出）。**VoxCPM 不能这样做**：加载 2B 权重要几十秒，每句重载一次等于每句话都卡半分钟。

所以 VoxCPM 走**常驻 python 进程**：进程启动后加载一次模型，之后通过 stdin/stdout 收发 JSON 行。这样：

- 第一次朗读付一次加载成本，之后每句只付推理成本。
- 停止播音 = 杀进程或发停止指令，不需要重载。

这个常驻进程同时是**模型进程与播放器**：与 Edge 路径一致，音频播放仍在 python 侧（`sounddevice`），`.NET` 侧不引入音频播放依赖。

### 4.2 引擎契约：从「Edge 专属接口」抽成「通用 TTS 引擎接口」

现在 `IEdgeTtsEngine` 是 Edge 专属的（`IsAvailable` / `TrySpeakAsync` / `Stop`），`TtsService` 里靠 `voiceId.StartsWith("zh-CN-")` 判断走 Edge 还是走 Windows。

加第三个引擎时，如果继续用字符串前缀判断，会变成三段 `if`。按 **principle-model-the-domain**，音色与引擎的归属关系应当由数据本身表达，而不是靠散落的条件判断。所以：

- `TtsVoice` 增加一个 `Engine` 字段（枚举），标明这个音色归哪个引擎。
- `TtsService` 按 `voice.Engine` 分派，而不是按字符串前缀。
- `IEdgeTtsEngine` 泛化成 `ITtsEngine`，Edge 与 VoxCPM 各自实现。

这样音色与引擎的对应关系写在一处，可枚举、可测试，也不用动 Edge 现有行为。

### 4.3 音色标识

音色标识要同时满足：写进 `settings.json` 后重启仍认得；Edge 的既有标识不能改（老用户的 `settings.json` 里存着 `zh-CN-YunxiNeural`）。

| 引擎 | 音色标识 | 说明 |
|---|---|---|
| Edge | `zh-CN-YunxiNeural` 等 | **保持原样**，不改老用户设置 |
| Windows | `Microsoft Kangkang` 等 | 保持原样 |
| VoxCPM 设计音色 | `voxcpm-design:<描述>` | 描述即音色定义 |
| VoxCPM 克隆音色 | `voxcpm-clone:<参考音频路径>` | 指向用户自备的音频文件 |

VoxCPM 的标识带参数（描述或参考音频路径），因为上游 `generate` 本来就要收这两样。标识里带路径这一条要注意：`settings.json` 是用户私有文件（`%LOCALAPPDATA%\LocalDm`），参考音频路径写在里面不出本机，符合宪章第 V 条。

### 4.4 降级链

现在 Edge 失败降级到 Windows 本地语音。加入 VoxCPM 后的顺序：

```
用户选的音色 → 该引擎 → （失败）→ Edge-TTS → （失败）→ Windows 本地语音
```

**降级必须存在**：VoxCPM 是可选增强，没装依赖的机器必须照常能玩。降级到 Edge 而不是直接到 Windows，是因为 Edge 质量更高且已经是既有首选。

## 5. 实施范围

### 做（已实施）

1. `src/LocalDm.Core/Ports/ExternalPorts.cs`：`TtsVoice` 加 `Engine` 字段；新增 `TtsEngineKind` 枚举。✓
2. `src/LocalDm.Infrastructure/Voice/ITtsEngine.cs`：泛化后的引擎契约。✓
   另加 `EdgeTtsVoiceEngine.cs`（Edge 的新实现）与 `TtsVoiceId.cs`（VoxCPM 标识解析）。✓
3. `src/LocalDm.Infrastructure/Voice/VoxCpmEngine.cs`：探测依赖、拉起常驻 python 进程、发 JSON 行、收结果。✓
4. `src/LocalDm.Infrastructure/Voice/voxcpm_worker.py`：常驻 worker。stdin 收 `{"text": ..., "design": ..., "reference_wav": ...}`，播放后回一行状态。✓
5. `src/LocalDm.Infrastructure/Voice/TtsService.cs`：按 `voice.Engine` 分派，三级降级链。✓
6. 设置项：`ToolSettings` 四个字段、`SettingsStore` 序列化、`SetupViewModel`、`SetupView.xaml`、`AppComposition` 装配。✓
7. 测试：`TtsEngineRoutingTests`、`VoxCpmVoiceTests`，不依赖本机是否装了 python。✓

### 不做

- 不做 LoRA 微调界面（需要训练环境，超出本软件范围）。
- 不做批量合成与音频导出（那是工具，不是主持软件）。
- 不把参考音频或模型权重放进仓库（宪章第 V 条）。

## 6. 探针结果

见 `docs/evidence/us3/voxcpm-tts-integration.md`。三档模型推理全部失败，
根因是本机系统提交额度耗尽而非显存不足；引擎运行时行为（依赖探测、
worker 握手失败回合法 JSON 即降级信号）已实测通过。

## 7. 风险

| 风险 | 应对 |
|---|---|
| 本机提交额度不足，跑不动任何档位 | 已定位根因（页面文件固定 32GB 且已占 16GB，其余被 qemu 与 22 个 claude 进程占掉）。释放内存或扩大页面文件后可重跑探针。**这条是环境问题，不是代码问题** |
| 显存不够跑 VoxCPM2 | 模型目录可配置：换机器或换档位即可，不用改代码 |
| 首次加载慢（几十秒） | worker 握手有 5 分钟上限；探测不加载模型，不拖慢界面 |
| Windows 依赖必须 `--no-deps` 手工装 | 写清安装步骤；探测不到就报「未安装」并降级，不假装可用 |
| 参考音频是用户私有素材 | 只存路径，不拷进仓库、不外发 |
| 推理时 GPU 占用影响其它程序 | 属用户机器上的正常占用，不额外隔离 |

## 8. 验收口径

- `dotnet build LocalDm.slnx`、`dotnet test tests/LocalDm.Tests`、`dotnet test tests/LocalDm.UiTests` 全通过。**已达成**（单元 386 通过 / 0 失败 / 7 跳过，界面 34 通过 / 0 失败）。
- 未装 VoxCPM 的机器上，音色列表与朗读行为与现在一致（降级链不回归）。**已达成**（用替身测）。
- 不在本机装 VoxCPM 的测试环境不因此变红。**已达成**。
- 装了 VoxCPM 的机器上，能用设计音色与克隆音色各读出一句中文，且音频时长与文本长度相符。**未达成**：本机提交额度不足，见证据文档的「未执行与受阻」。
