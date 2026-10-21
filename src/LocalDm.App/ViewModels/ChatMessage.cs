namespace LocalDm.App.ViewModels;

/// <summary>对话里一条消息属于谁。</summary>
public enum ChatAuthor
{
    /// <summary>本局主持（模型生成的主持词）。</summary>
    Host = 0,

    /// <summary>玩家说出的话。</summary>
    Player = 1,

    /// <summary>程序产生的阶段事件：进入某环节、发放材料、提交票数、公开结局。</summary>
    Event = 2,
}

/// <summary>一条主持回复的朗读进度。就地显示，不用另外一块常驻状态栏。</summary>
public enum ChatSpeechState
{
    /// <summary>不需要朗读（玩家消息与阶段事件）。</summary>
    NotApplicable = 0,

    /// <summary>已生成文字，还没开始朗读。</summary>
    Ready = 1,

    /// <summary>正在朗读。</summary>
    Speaking = 2,

    /// <summary>已读完。</summary>
    Spoken = 3,

    /// <summary>被主持人手动停止。</summary>
    Stopped = 4,

    /// <summary>朗读失败（文字仍保留）。</summary>
    Failed = 5,
}

/// <summary>
/// 对话里的一条消息。玩家输入、主持回复与阶段事件按时间顺序排在同一个列表里，
/// 消息之间靠归属区分，不靠分成两块区域。
/// </summary>
public sealed class ChatMessage : ObservableObject
{
    private ChatSpeechState _speechState;
    private string _speechNote = string.Empty;

    public ChatMessage(ChatAuthor author, string text, string? time = null, string speechNote = "")
    {
        Author = author;
        Text = text;
        Time = time ?? DateTime.Now.ToString("HH:mm");
        Id = Guid.NewGuid().ToString("N");
        _speechNote = speechNote;
        _speechState = author == ChatAuthor.Host ? ChatSpeechState.Ready : ChatSpeechState.NotApplicable;
    }

    /// <summary>本条消息的一次性标识，用于给重播请求一个稳定的 requestId。</summary>
    public string Id { get; }

    public ChatAuthor Author { get; }

    public string Text { get; }

    public string Time { get; }

    public bool IsHost => Author == ChatAuthor.Host;

    /// <summary>由模型生成的回复，才作为后续模型对话历史；界面引导文字不冒充模型回答。</summary>
    public bool IsModelReply { get; internal init; }

    public bool IsPlayer => Author == ChatAuthor.Player;

    public bool IsEvent => Author == ChatAuthor.Event;

    /// <summary>作者的显示名。事件没有作者，用一条细线代替。</summary>
    public string AuthorName => Author switch
    {
        ChatAuthor.Host => "本局主持",
        ChatAuthor.Player => "玩家",
        _ => string.Empty,
    };

    /// <summary>就地朗读状态，例如「正在朗读…」「已朗读」「已停止」「朗读失败：…」。</summary>
    public string SpeechNote
    {
        get => _speechNote;
        private set => SetProperty(ref _speechNote, value);
    }

    public ChatSpeechState SpeechState
    {
        get => _speechState;
        private set
        {
            if (SetProperty(ref _speechState, value))
            {
                OnPropertyChanged(nameof(IsSpeaking));
                OnPropertyChanged(nameof(CanReplay));
                ReplayCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsSpeaking => SpeechState == ChatSpeechState.Speaking;

    /// <summary>
    /// 能不能重播：只有主持回复可以，而且不能在朗读中。
    /// 「已生成但没读」（自动朗读关闭）同样可以重播 —— 那正是需要它的时候。
    /// </summary>
    public bool CanReplay => IsHost && SpeechState != ChatSpeechState.Speaking;

    /// <summary>逐条重播命令。由主持台在加入消息时接线。</summary>
    public RelayCommand? ReplayCommand { get; internal set; }

    public ChatMessage SetSpeech(ChatSpeechState state, string note)
    {
        SpeechState = state;
        SpeechNote = note;
        ReplayCommand?.RaiseCanExecuteChanged();
        return this;
    }
}
