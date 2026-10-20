using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using LocalDm.Core.Domain;
using LocalDm.Core.Ports;

namespace LocalDm.Infrastructure.Storage;

/// <summary>
/// 一次完整会话运行的装配：数据根目录、SQLite 存档与设置。
/// 运行数据不存安装目录。
/// </summary>
public sealed class SessionContext
{
    private readonly SessionStore _store;

    private SessionContext(string dataRoot, SessionStore store, SettingsStore settings)
    {
        DataRoot = dataRoot;
        _store = store;
        Settings = settings;
    }

    public string DataRoot { get; }

    public SettingsStore Settings { get; }

    public ISessionStore Store => _store;

    /// <summary>
    /// 运行数据根目录：%LOCALAPPDATA%\LocalDm，可用环境变量 LOCALDM_DATA_ROOT 覆盖（测试用）。
    /// </summary>
    public static string ResolveDataRoot()
    {
        var overridden = Environment.GetEnvironmentVariable("LOCALDM_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return overridden;
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "LocalDm");
    }

    public static async Task<SessionContext> OpenAsync(
        string? dataRoot,
        CancellationToken cancellationToken = default)
    {
        var root = dataRoot ?? ResolveDataRoot();
        Directory.CreateDirectory(root);

        var store = new SessionStore(Path.Combine(root, "sessions.db"));
        await store.InitializeAsync(cancellationToken);

        var settings = new SettingsStore(Path.Combine(root, "settings.json"), Path.Combine(root, "secrets"));
        return new SessionContext(root, store, settings);
    }

    public void Dispose() => _store.Dispose();
}

/// <summary>
/// 设置存储。caller key 使用 Windows DPAPI（CurrentUser 范围）加密后落盘，
/// 普通 JSON、日志与测试输出中不出现明文 key。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SettingsStore
{
    /// <summary>
    /// 所有写入共用一把锁：主持台切音色与设置页保存可能同时写同一个文件，
    /// 不串行化会写出交错或截断的 JSON。
    /// </summary>
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    private readonly string _settingsPath;
    private readonly string _secretDirectory;

    public SettingsStore(string settingsPath, string secretDirectory)
    {
        _settingsPath = settingsPath;
        _secretDirectory = secretDirectory;
    }

    public string SettingsPath => _settingsPath;

    public async Task<ToolSettings> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_settingsPath))
        {
            return ToolSettings.Default;
        }

        var json = await File.ReadAllTextAsync(_settingsPath, cancellationToken);
        return Parse(json);
    }

    /// <summary>
    /// 同步读取当前设置。界面每次动作前调用，保证「刚在设置页保存的 model ID」立刻生效，
    /// 不会因为启动时缓存了旧值而提示未选择模型。
    /// </summary>
    public ToolSettings Load() =>
        File.Exists(_settingsPath) ? Parse(File.ReadAllText(_settingsPath)) : ToolSettings.Default;

    private ToolSettings Parse(string json)
    {
        SettingsDto dto;
        try
        {
            dto = System.Text.Json.JsonSerializer.Deserialize<SettingsDto>(json) ?? new SettingsDto();
        }
        catch (System.Text.Json.JsonException)
        {
            // 设置文件损坏（写入过程中断电等）不应该让整个应用起不来。
            // 但也不能就这么把默认值写回去 —— 那会把还能人工修复的内容覆盖掉。
            // 先把坏文件改名备份，再退回默认设置。
            System.Diagnostics.Debug.WriteLine("[Settings] settings.json 解析失败，已退回默认设置。");
            try
            {
                var backup = _settingsPath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Move(_settingsPath, backup, overwrite: true);
            }
            catch (IOException)
            {
                // 备份失败也不影响启动。
            }

            return ToolSettings.Default;
        }

        var uri = Uri.TryCreate(dto.MagpieBaseUri, UriKind.Absolute, out var parsed)
            ? parsed
            : ToolSettings.DefaultMagpieUri;
        return new ToolSettings
        {
            MagpieBaseUri = uri,
            ModelId = dto.ModelId,
            CallerKeyRef = dto.CallerKeyRef,
            MineruExecutable = dto.MineruExecutable,
            VoiceId = dto.VoiceId,
            InputHint = dto.InputHint ?? ToolSettings.Default.InputHint,
            // 旧设置文件没有这一项：默认自动朗读，与旧行为一致。
            AutoRead = dto.AutoRead ?? true,
            // 旧设置文件同样没有上下文长度：按默认值处理，不让旧存档开不了局。
            ContextWindowTokens = dto.ContextWindowTokens ?? ToolSettings.DefaultContextWindowTokens,
            // 旧设置文件没有界面缩放与对话宽度：保持原来的观感（1.0 倍、标准宽度）。
            UiScale = dto.UiScale ?? ToolSettings.DefaultUiScale,
            ChatWidthMode = dto.ChatWidthMode ?? ChatWidthMode.Standard,
        };
    }

    /// <summary>
    /// 异步保存设置。写临时文件再原子替换，避免中途退出留下半截 JSON。
    /// </summary>
    public async Task SaveAsync(ToolSettings settings, CancellationToken cancellationToken)
    {
        var payload = Serialize(settings);
        await WriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WriteAtomically(payload);
        }
        finally
        {
            WriteLock.Release();
        }
    }

    /// <summary>
    /// 同步保存设置，供界面线程的即时保存使用（例如主持台切换音色）。
    /// 与 <see cref="SaveAsync"/> 共用同一份序列化与写盘逻辑，两条路径不会分叉。
    /// </summary>
    public void Save(ToolSettings settings)
    {
        var payload = Serialize(settings);
        WriteLock.Wait();
        try
        {
            WriteAtomically(payload);
        }
        finally
        {
            WriteLock.Release();
        }
    }

    /// <summary>设置项 → DTO。两条保存路径共用，避免新增设置项时只改一处。</summary>
    private static SettingsDto ToDto(ToolSettings settings) => new()
    {
        MagpieBaseUri = settings.MagpieBaseUri.ToString(),
        ModelId = settings.ModelId,
        CallerKeyRef = settings.CallerKeyRef,
        MineruExecutable = settings.MineruExecutable,
        VoiceId = settings.VoiceId,
        InputHint = settings.InputHint,
        AutoRead = settings.AutoRead,
        ContextWindowTokens = settings.ContextWindowTokens,
        UiScale = settings.UiScale,
        ChatWidthMode = settings.ChatWidthMode,
    };

    private static string Serialize(ToolSettings settings)
    {
        settings.Validate();
        return System.Text.Json.JsonSerializer.Serialize(ToDto(settings), SerializerOptions);
    }

    /// <summary>写临时文件后原子替换目标文件：中途失败不会破坏原有设置。</summary>
    private void WriteAtomically(string payload)
    {
        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 临时名要唯一：同机同时跑两个实例（便携版 + 安装版）时，
        // 固定名字会互相覆盖对方的半截内容。
        var tempPath = Path.Combine(
            string.IsNullOrEmpty(directory) ? "." : directory,
            $".{Path.GetFileName(_settingsPath)}.{Path.GetRandomFileName()}.tmp");

        try
        {
            File.WriteAllText(tempPath, payload, Encoding.UTF8);
            File.Move(tempPath, _settingsPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (IOException)
            {
                // 残留的临时文件不影响下次写入。
            }
        }
    }

    /// <summary>
    /// 保存 caller key：DPAPI CurrentUser 加密后写入 secrets 目录，返回可序列化的引用标识。
    /// 明文只存在于本次调用的内存中，不从 Magpie 上游配置读取。
    /// </summary>
    public string SaveCallerKey(string plainKey)
    {
        if (string.IsNullOrWhiteSpace(plainKey))
        {
            throw new DomainRuleException(DomainErrorCode.INVALID_INPUT, "caller key 不能为空。");
        }

        Directory.CreateDirectory(_secretDirectory);
        var keyRef = $"dpapi-{Guid.NewGuid():N}";
        var protectedBytes = System.Security.Cryptography.ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plainKey),
            optionalEntropy: null,
            System.Security.Cryptography.DataProtectionScope.CurrentUser);
        File.WriteAllBytes(Path.Combine(_secretDirectory, keyRef + ".secure"), protectedBytes);
        return keyRef;
    }

    /// <summary>读取 caller key 明文，仅用于发起本机 HTTP 请求。</summary>
    public string? LoadCallerKey(string? keyRef)
    {
        if (string.IsNullOrWhiteSpace(keyRef))
        {
            return null;
        }

        var path = Path.Combine(_secretDirectory, keyRef + ".secure");
        if (!File.Exists(path))
        {
            return null;
        }

        var protectedBytes = File.ReadAllBytes(path);
        try
        {
            var plain = System.Security.Cryptography.ProtectedData.Unprotect(protectedBytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            // 换机或换用户后 DPAPI 解不开：视为未配置，不静默使用别的凭据
            return null;
        }
    }

    public static bool IsSecretMaterialPersistedAsPlainText(string settingsPath, string plainKey)
    {
        if (!File.Exists(settingsPath))
        {
            return false;
        }

        var text = File.ReadAllText(settingsPath);
        return text.Contains(plainKey, StringComparison.Ordinal);
    }

    private static readonly System.Text.Json.JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private sealed class SettingsDto
    {
        public string? MagpieBaseUri { get; set; }

        public string? ModelId { get; set; }

        public string? CallerKeyRef { get; set; }

        public string? MineruExecutable { get; set; }

        public string? VoiceId { get; set; }

        public string? InputHint { get; set; }

        /// <summary>可空：旧设置文件没有这一项时按默认「自动朗读」处理。</summary>
        public bool? AutoRead { get; set; }

        /// <summary>可空：旧设置文件没有这一项时按默认上下文长度处理。</summary>
        public int? ContextWindowTokens { get; set; }

        /// <summary>可空：旧设置文件没有这一项时按 1.0 倍处理。</summary>
        public double? UiScale { get; set; }

        /// <summary>可空：旧设置文件没有这一项时按标准宽度处理。</summary>
        public ChatWidthMode? ChatWidthMode { get; set; }
    }
}

/// <summary>
/// 包指纹工具：把源文件 hash/页数/顺序与来源索引归一化成稳定 hash。
/// </summary>
public static class ManifestFingerprint
{
    public static string Compute(IEnumerable<SourceFile> files)
    {
        var builder = new StringBuilder();
        foreach (var file in files.OrderBy(f => f.FileId, StringComparer.Ordinal))
        {
            builder.Append(file.FileId)
                .Append('|')
                .Append(file.ContentHash)
                .Append('|')
                .Append(file.PageCount)
                .Append('|')
                .Append((int)file.Origin)
                .Append('\n');
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(bytes);
    }

    public static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>对内存文本计算 SHA-256（来源文本 hash）。</summary>
    public static string ComputeSha256Text(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
