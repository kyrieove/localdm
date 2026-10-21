using System.Text;

namespace LocalDm.Core.Tablet;

/// <summary>
/// 合成角色本：探针与自动化测试共用的固定样本。
/// 文字是 Codex 为阅读器比较写的合成内容，与任何真实剧本无关。
/// </summary>
public static class SyntheticRoleBook
{
    public const string Title = "雨停之前";

    public static RoleBookText Create()
    {
        var chapters = new[]
        {
            Chapter("ch-1", "抵达", "你抵达小镇的时候，雨刚刚停。站台上的屋檐还在滴水，一滴落下，另一滴紧跟着，把午后的安静敲出很轻的回声。远处的山藏在雾里，只露出一条柔和的轮廓。",
                "你在长椅上坐了一会儿，没有急着打开行李。木头被雨气浸得发凉，空气里有潮湿的树叶和新鲜面包的味道。站台对面的小店已经亮起灯，门口挂着一块手写的牌子，字迹有些褪色，却仍旧端正。",
                "车站管理员从屋里走出来，把一盆绿植搬到屋檐外。他抬头看了看天，说这样的雨往往还会回来。你点点头，问他镇上的图书室怎么走。他指向街道尽头，说顺着河边走，看到一棵很大的树就到了。"),
            Chapter("ch-2", "旧书与午后", "图书室就在大树后面。推门进去，首先看见的是一张很长的木桌，桌面擦得干净，靠窗的一侧放着几只陶瓷杯。窗外的树叶挡住了一半阳光，光落在纸页上，缓慢地移动。",
                "你在书架之间走了一圈。这里的书没有按崭新的标准排列，有些封面已经磨损，有些书脊上的字需要凑近才能看清。每一本书似乎都被人认真读过，又被安静地放回原处，等待下一位读者。",
                "管理图书室的人递给你一张空白的借阅卡，说如果愿意，可以先在这里读一会儿。你接过铅笔，在姓名一栏写下自己的名字。笔尖划过纸面的声音很轻，像某种久违的开始。"),
            Chapter("ch-3", "沿河散步", "傍晚时，你把书留在桌上，出去散步。河边的路被晒干了一半，另一半仍然留着浅浅的水迹。鞋底踩上去，会发出短促的声音，很快又被风吹散。",
                "桥边坐着两个下棋的人。他们很少说话，每下一步，都像要先听一听河水的意见。你站在旁边看了片刻，看不懂局势，却不觉得着急。有些事情，做一个暂时的旁观者也很好。",
                "镇上的钟响了六下。太阳照在对岸的墙上，墙面有一道很细的裂纹，一株植物从那里长出来。你把这一幕记在心里，却没有拿出相机。"),
        };

        var book = new RoleBookText
        {
            RoleId = "synthetic-role",
            Chapters = chapters,
            SourcePageAssetIds = [],
            Confidence = new RoleBookTextConfidence
            {
                TotalBlocks = chapters.Sum(c => c.Blocks.Count),
                EmptyOrSuspectBlocks = 0,
                HasSourcePages = false,
            },
        };
        return book with
        {
            Confidence = book.Confidence with { TotalCharacters = RoleBookPlainText.CharacterCount(book) },
        };
    }

    /// <summary>一个三章样本：章题 + 三个段落，每章结构一致，便于断言章节边界。</summary>
    private static RoleBookChapter Chapter(string id, string title, params string[] paragraphs)
    {
        var blocks = new List<RoleBookBlock>
        {
            new()
            {
                BlockId = $"{id}-title",
                Kind = RoleBookBlockKind.ChapterTitle,
                Text = title,
            },
        };
        blocks.AddRange(paragraphs.Select((text, index) => new RoleBookBlock
        {
            BlockId = $"{id}-p{index + 1}",
            Kind = RoleBookBlockKind.Paragraph,
            Text = text,
        }));

        return new RoleBookChapter { ChapterId = id, Title = title, Blocks = blocks };
    }
}
