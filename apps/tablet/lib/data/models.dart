// 字段名与 /tablet/* 接口逐字一致（假数据就是合成服务的真实返回，见 assets/fixtures）。

class Role {
  Role.fromJson(Map<String, dynamic> j)
      : roleId = j['roleId'] as String,
        displayName = j['displayName'] as String;
  final String roleId;
  final String displayName;
}

class ClueCard {
  ClueCard.fromJson(Map<String, dynamic> j)
      : cardId = j['cardId'] as String,
        displayName = j['displayName'] as String,
        groupName = j['groupName'] as String,
        sourceOrder = j['sourceOrder'] as int,
        isAvailable = j['isAvailable'] as bool,
        notReadyReason = j['notReadyReason'] as String?;
  final String cardId;
  final String displayName;
  final String groupName;
  final int sourceOrder;
  bool isAvailable;
  final String? notReadyReason;
}

class Catalog {
  Catalog.fromJson(Map<String, dynamic> j)
      : storyTitle = j['storyTitle'] as String,
        roles = [for (final r in j['roles'] as List) Role.fromJson(r)],
        cards = [for (final c in j['cards'] as List) ClueCard.fromJson(c)],
        claimedRoles = {for (final r in j['claimedRoles'] as List) r as String};
  final String storyTitle;
  final List<Role> roles;
  final List<ClueCard> cards;
  final Set<String> claimedRoles;
}

class Block {
  Block.fromJson(Map<String, dynamic> j)
      : kind = j['kind'] as String,
        text = j['text'] as String,
        sourcePageNumber = j['sourcePageNumber'] as int?;
  final String kind;
  final String text;
  final int? sourcePageNumber;
}

class Chapter {
  Chapter.fromJson(Map<String, dynamic> j)
      : title = j['title'] as String,
        blocks = [for (final b in j['blocks'] as List) Block.fromJson(b)];
  final String title;
  final List<Block> blocks;
}

class RoleBook {
  RoleBook.fromJson(Map<String, dynamic> j)
      : storyTitle = j['storyTitle'] as String,
        chapters = [for (final c in j['chapters'] as List) Chapter.fromJson(c)];
  final String storyTitle;
  final List<Chapter> chapters;
}

class PublicMessage {
  PublicMessage.fromJson(Map<String, dynamic> j)
      : sequence = j['sequence'] as int,
        author = j['author'] as String,
        kind = j['kind'] as String,
        text = j['text'] as String,
        createdAt = DateTime.parse(j['createdAt'] as String);
  PublicMessage(this.sequence, this.author, this.kind, this.text) : createdAt = DateTime.now();
  final int sequence;
  final String author; // Host / Player / System
  final String kind; // HostReply / PlayerSpeech / FlowEvent
  final String text;
  final DateTime createdAt;
}

class PhaseItem {
  PhaseItem.fromJson(Map<String, dynamic> j)
      : title = j['title'] as String,
        status = j['status'] as String;
  final String title;
  final String status; // Done / Current / Upcoming
}

class TableUi {
  TableUi.fromJson(Map<String, dynamic> j)
      : phaseTitle = j['phaseTitle'] as String,
        phaseIndex = j['phaseIndex'] as int,
        phaseCount = j['phaseCount'] as int,
        hint = j['hint'] as String?,
        phases = [for (final p in j['phases'] as List) PhaseItem.fromJson(p)],
        controlsNote = j['controlsNote'] as String,
        pendingMaterialCount = j['pendingMaterialCount'] as int,
        releasedMaterialCount = j['releasedMaterialCount'] as int;
  final String phaseTitle;
  final int phaseIndex;
  final int phaseCount;
  final String? hint;
  final List<PhaseItem> phases;
  final String controlsNote;
  final int pendingMaterialCount;
  final int releasedMaterialCount;
}
