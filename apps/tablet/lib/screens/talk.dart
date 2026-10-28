import 'package:flutter/material.dart';

import '../data/models.dart';
import '../main.dart';

/// 公开对话：全桌可见，认领角色后可以发言（ADR 0012 决定 4，2026-10-28 修订）。
class TalkScreen extends StatefulWidget {
  const TalkScreen({super.key});

  @override
  State<TalkScreen> createState() => _TalkScreenState();
}

class _TalkScreenState extends State<TalkScreen> {
  final _scroll = ScrollController();

  @override
  void dispose() {
    _scroll.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final scheme = Theme.of(context).colorScheme;
    final messages = app.messages.reversed.toList(); // 倒序列表：最新的贴底，新消息来了自然出现在底部
    return Column(children: [
      Padding(
        padding: const EdgeInsets.fromLTRB(28, 20, 72, 12),
        child: Row(children: [
          Text('公开对话', style: Theme.of(context).textTheme.headlineSmall?.copyWith(fontWeight: FontWeight.w600)),
          const SizedBox(width: 14),
          _HostStatus(speaking: app.hostSpeaking, busy: app.hostBusy),
        ]),
      ),
      Expanded(
        child: Center(
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 760),
            child: ListView.builder(
              controller: _scroll,
              reverse: true,
              padding: const EdgeInsets.fromLTRB(28, 8, 28, 16),
              itemCount: messages.length,
              itemBuilder: (context, i) => _Message(m: messages[i]),
            ),
          ),
        ),
      ),
      Container(
        width: double.infinity,
        padding: const EdgeInsets.symmetric(vertical: 12, horizontal: 28),
        color: scheme.surfaceContainerLow,
        child: Center(child: ConstrainedBox(constraints: const BoxConstraints(maxWidth: 760), child: const _SayBar())),
      ),
    ]);
  }
}

/// 发言条：发出的话全桌可见，由电脑署名进 AI 对话。语音用键盘自带的听写，App 不录音（ADR 0012 决定 4）。
class _SayBar extends StatefulWidget {
  const _SayBar();

  @override
  State<_SayBar> createState() => _SayBarState();
}

class _SayBarState extends State<_SayBar> {
  final _text = TextEditingController();
  bool _sending = false;

  @override
  void dispose() {
    _text.dispose();
    super.dispose();
  }

  Future<void> _send() async {
    final text = _text.text.trim();
    if (text.isEmpty || _sending) return;
    setState(() => _sending = true);
    final result = await AppScope.of(context).say(text);
    if (!mounted) return;
    setState(() => _sending = false);
    if (result.ok) _text.clear();
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(result.message)));
  }

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context);
    final locked = app.hostBusy || _sending;
    return Row(children: [
      Expanded(
        child: TextField(
          controller: _text,
          enabled: !locked,
          maxLength: 300,
          minLines: 1,
          maxLines: 4,
          textInputAction: TextInputAction.send,
          onSubmitted: (_) => _send(),
          decoration: InputDecoration(
            hintText: locked ? '主持正在回应' : '对主持人说…（可用键盘上的话筒听写）',
            counterText: '',
            border: const OutlineInputBorder(borderRadius: BorderRadius.all(Radius.circular(24))),
            contentPadding: const EdgeInsets.symmetric(horizontal: 18, vertical: 12),
          ),
        ),
      ),
      const SizedBox(width: 10),
      if (app.hostSpeaking)
        IconButton.filledTonal(onPressed: app.stopSpeaking, tooltip: '停止朗读', icon: const Icon(Icons.stop_rounded))
      else
        IconButton.filled(onPressed: locked ? null : _send, tooltip: '发送', icon: const Icon(Icons.send_rounded)),
    ]);
  }
}

class _Message extends StatelessWidget {
  const _Message({required this.m});
  final PublicMessage m;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    if (m.kind == 'FlowEvent') {
      return Padding(
        padding: const EdgeInsets.symmetric(vertical: 14),
        child: Row(children: [
          Expanded(child: Divider(color: scheme.outlineVariant)),
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 12),
            child: Text(m.text, style: TextStyle(color: scheme.primary, fontWeight: FontWeight.w600, fontSize: 14)),
          ),
          Expanded(child: Divider(color: scheme.outlineVariant)),
        ]),
      );
    }
    final host = m.author == 'Host';
    final time = '${m.createdAt.toLocal().hour.toString().padLeft(2, '0')}:${m.createdAt.toLocal().minute.toString().padLeft(2, '0')}';
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
        CircleAvatar(
          radius: 20,
          backgroundColor: host ? scheme.primary : scheme.surfaceContainerHighest,
          child: Icon(host ? Icons.record_voice_over_rounded : Icons.person_rounded, size: 20, color: host ? scheme.onPrimary : scheme.onSurfaceVariant),
        ),
        const SizedBox(width: 14),
        Expanded(
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text('${host ? '主持人' : '玩家'} · $time', style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 13)),
            const SizedBox(height: 4),
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
              decoration: BoxDecoration(
                color: host ? scheme.surfaceContainerHigh : scheme.surfaceContainer,
                borderRadius: const BorderRadius.only(
                  topRight: Radius.circular(18),
                  bottomLeft: Radius.circular(18),
                  bottomRight: Radius.circular(18),
                  topLeft: Radius.circular(4),
                ),
              ),
              child: Text(m.text, style: TextStyle(fontSize: 18, height: 1.6, color: scheme.onSurface)),
            ),
          ]),
        ),
      ]),
    );
  }
}

/// 主持人在说话时三根竖条跳动，忙时转圈；空闲时不显示，不占注意力。
class _HostStatus extends StatefulWidget {
  const _HostStatus({required this.speaking, required this.busy});
  final bool speaking;
  final bool busy;

  @override
  State<_HostStatus> createState() => _HostStatusState();
}

class _HostStatusState extends State<_HostStatus> with SingleTickerProviderStateMixin {
  late final _c = AnimationController(vsync: this, duration: const Duration(milliseconds: 900))..repeat();

  @override
  void dispose() {
    _c.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    if (!widget.speaking && !widget.busy) return const SizedBox();
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
      decoration: BoxDecoration(color: scheme.primaryContainer, borderRadius: BorderRadius.circular(20)),
      child: Row(mainAxisSize: MainAxisSize.min, children: [
        if (widget.speaking)
          AnimatedBuilder(
            animation: _c,
            builder: (context, _) => Row(children: [
              for (var i = 0; i < 3; i++)
                Container(
                  width: 3,
                  height: 6 + 10 * (0.5 + 0.5 * (((_c.value + i / 3) % 1) < 0.5 ? ((_c.value + i / 3) % 1) * 2 : 2 - ((_c.value + i / 3) % 1) * 2)),
                  margin: const EdgeInsets.symmetric(horizontal: 1.5),
                  decoration: BoxDecoration(color: scheme.onPrimaryContainer, borderRadius: BorderRadius.circular(2)),
                ),
            ]),
          )
        else
          SizedBox(width: 14, height: 14, child: CircularProgressIndicator(strokeWidth: 2, color: scheme.onPrimaryContainer)),
        const SizedBox(width: 8),
        Text(widget.speaking ? '主持人正在说' : '主持人在想', style: TextStyle(color: scheme.onPrimaryContainer, fontSize: 14, fontWeight: FontWeight.w600)),
      ]),
    );
  }
}
