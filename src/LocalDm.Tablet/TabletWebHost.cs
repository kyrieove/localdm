using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalDm.Core.Tablet;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;

namespace LocalDm.Tablet;

public static class TabletWebHost
{
    private static readonly JsonSerializerOptions UiJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static WebApplication Create(
        ITabletContent content,
        IPublicFeedSource source,
        string[] args,
        string contentRoot,
        ITabletSpeech? speech = null,
        TimeProvider? time = null,
        ITabletUiSource? ui = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ApplicationName = typeof(TabletWebHost).Assembly.GetName().Name,
            ContentRootPath = contentRoot,
            WebRootPath = Path.Combine(contentRoot, "wwwroot"),
        });
        if (args.Contains("--urls") is false && builder.Configuration["urls"] is not { Length: > 0 })
        {
            builder.WebHost.UseUrls("http://127.0.0.1:5099");
        }

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();
        builder.Services.AddSingleton(content);
        builder.Services.AddSingleton<TabletSpikeState>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(source);
        builder.Services.AddSingleton(speech ?? new NoTabletSpeech());
        builder.Services.AddSingleton(time ?? TimeProvider.System);
        builder.Services.AddSingleton<ITabletUiSource>(ui ?? new EmptyTabletUiSource());
        builder.Services.AddSingleton<TabletSpeechService>();
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/tablet"))
            {
                context.Response.Headers.CacheControl = "no-store";
            }
            if (context.Request.Path == "/" && TabletSessions.KeyFor(context) is null)
            {
                TabletSessions.Issue(context);
            }
            await next(context);
        });
        app.UseAntiforgery();

        // Debug 下 wwwroot 留在源目录；内容根指到输出目录时把它作为静态文件源。
        // blazor.web.js 在 Debug 输出里不存在（只在 publish 产物里），
        // 所以测试与真实探针都用 publish 产物运行，见 TabletBlazorHost。
        var sharedFrameworkRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "src", "LocalDm.Tablet", "wwwroot"));
        var staticFileOptions = new StaticFileOptions
        {
            OnPrepareResponse = context =>
            {
                var extension = Path.GetExtension(context.File.Name);
                if (extension.Equals(".css", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".js", StringComparison.OrdinalIgnoreCase))
                {
                    context.Context.Response.Headers.CacheControl = "no-cache";
                }
            },
        };
        if (Directory.Exists(Path.Combine(builder.Environment.ContentRootPath, "wwwroot")))
        {
            app.UseStaticFiles(staticFileOptions);
        }
        else if (Directory.Exists(sharedFrameworkRoot))
        {
            staticFileOptions.FileProvider = new PhysicalFileProvider(sharedFrameworkRoot);
            app.UseStaticFiles(staticFileOptions);
        }

        // 释放角色：让别的会话可以认领；同时清掉本会话的阅读状态。
        app.MapPost("/tablet/release", (TabletSpikeState state, HttpContext context) =>
        {
            var key = TabletSessions.KeyFor(context);
            if (key is not null)
            {
                state.Release(key);
                TabletSessions.RaiseChanged();
            }

            return Results.Json(new { released = true });
        });

        // 认领角色：同一角色被别的会话占用时返回 409。网页按钮与测试都走这一个入口，
        // 保证「界面能点到」和「接口能被验证」是同一套逻辑。
        app.MapPost("/tablet/claim", (string? roleId, TabletSpikeState state, HttpContext context) =>
        {
            if (string.IsNullOrEmpty(roleId) || state.Roles.All(r => r.RoleId != roleId))
            {
                return Results.NotFound();
            }

            // 没有会话 Cookie 的请求先发一个：认领必须落到某个具体会话上，
            // 否则「谁认领的」无从谈起。
            var key = TabletSessions.KeyFor(context) ?? TabletSessions.Issue(context);
            var claimed = state.TryClaim(key, roleId);
            if (claimed) TabletSessions.RaiseChanged();
            return claimed
                ? Results.Json(new { claimed = true, roleId })
                : Results.Conflict(new { claimed = false, reason = "TAKEN" });
        });

        // 图片走 HTTP 响应，不 Base64 进连接消息；按 ID 解析，不按名称拼路径。
        // 同时给出「已被认领的角色」清单：真实设备上要能看到别人占了哪个角色。
        app.MapGet("/tablet/catalog", (ITabletContent content, TabletSpikeState state) => Results.Json(new
        {
            storyTitle = content.StoryTitle,
            roles = content.Roles,
            cards = content.Cards,
            claimedRoles = state.ClaimedRoles,
        }));

        // 卡面与卡背：按卡片 ID 取，取不到就 404，不拿整页图顶替。
        app.MapGet("/tablet/cards/{cardId}/front", (string cardId, ITabletContent content) =>
            CardImage(cardId, TabletCardSide.Front, content));

        app.MapGet("/tablet/cards/{cardId}/back", (string cardId, ITabletContent content) =>
            CardImage(cardId, TabletCardSide.Back, content));

        static IResult CardImage(string cardId, TabletCardSide side, ITabletContent content)
        {
            if (cardId.Contains('/') || cardId.Contains('\\') || cardId.Contains(".."))
            {
                return Results.NotFound();
            }

            if (!content.TryGetCardImage(cardId, side, out var bytes, out var contentType, out var failure))
            {
                // 「这一组暂停分发」与「没有这张卡」是两回事：
                // 前者 409（换组别的卡），后者 404。网页据此给不同的提示。
                return failure is TabletQueryFailure.GroupNotReady or TabletQueryFailure.AssetMissing
                    ? Results.Conflict(new { reason = failure.ToString() })
                    : Results.NotFound();
            }

            return Results.File(bytes, contentType);
        }

        // 角色本：按认领角色返回章节与块的纯文本。
        // 客户端不能传别人的角色；没有会话或没认领就什么都不给。
        app.MapGet("/tablet/role/book", (ITabletContent content, TabletSpikeState state, HttpContext context) =>
        {
            var role = SessionRole(state, context);
            if (role is null)
            {
                return Results.NotFound();
            }

            return Results.Json(new
            {
                roleId = role.RoleId,
                storyTitle = content.StoryTitle,
                chapters = content.RoleBooks[role.RoleId].Chapters.Select(c => new
                {
                    chapterId = c.ChapterId,
                    title = c.Title,
                    blocks = c.Blocks.Select(b => new
                    {
                        blockId = b.BlockId,
                        kind = b.Kind.ToString(),
                        text = b.Text,
                        sourcePageNumber = b.SourcePageNumber,
                        imageAssetId = b.ImageAssetId,
                        imageCaption = b.ImageCaption,
                    }),
                }),
                confidence = new
                {
                    totalBlocks = content.RoleBooks[role.RoleId].Confidence.TotalBlocks,
                    suspectBlocks = content.RoleBooks[role.RoleId].Confidence.EmptyOrSuspectBlocks,
                    hasSourcePages = content.RoleBooks[role.RoleId].Confidence.HasSourcePages,
                },
            });
        });

        // 阅读进度：按角色与会话保存，刷新、改字号、横竖屏切换后都能回到读到的位置。
        app.MapGet("/tablet/role/progress", (TabletSpikeState state, HttpContext context) =>
        {
            var role = SessionRole(state, context);
            if (role is null)
            {
                return Results.NotFound();
            }

            var session = state.Session(TabletSessions.KeyFor(context)!);
            return Results.Json(new
            {
                roleId = role.RoleId,
                characterOffset = session.ReadingOffset,
                settings = new
                {
                    fontSize = session.Settings.FontSize,
                    lineSpacing = session.Settings.LineSpacing,
                    focusMode = session.Settings.FocusMode,
                    preferText = session.Settings.PreferText,
                    sourcePageNumber = session.Settings.SourcePageNumber,
                    columns = session.Settings.Columns,
                },
            });
        });

        app.MapPost("/tablet/role/progress", (
            global::ReadingProgressRequest? request,
            TabletSpikeState state,
            HttpContext context) =>
        {
            var role = SessionRole(state, context);
            if (role is null || request is null)
            {
                return Results.NotFound();
            }

            var session = state.Session(TabletSessions.KeyFor(context)!);
            session.ReadingOffset = Math.Max(0, request.CharacterOffset);
            session.Settings = ReadingSettings.Clamp(new ReadingSettings
            {
                FontSize = request.FontSize,
                LineSpacing = request.LineSpacing,
                FocusMode = request.FocusMode,
                PreferText = request.PreferText ?? session.Settings.PreferText,
                SourcePageNumber = Math.Clamp(request.SourcePageNumber ?? session.Settings.SourcePageNumber,
                    1, Math.Max(1, state.Content.RoleBooks[role.RoleId].SourcePageAssetIds.Count)),
                Columns = request.Columns ?? session.Settings.Columns,
            });
            return Results.Json(new { saved = true, characterOffset = session.ReadingOffset });
        });

        // 原稿页核对入口：返回本人角色的原稿图，不给别人的，也不给物理路径。
        app.MapGet("/tablet/role/source", (ITabletContent content, TabletSpikeState state, HttpContext context) =>
            RoleSourcePage(content, state, context, 1));

        app.MapGet("/tablet/role/source/{page:int}", (int page, ITabletContent content, TabletSpikeState state, HttpContext context) =>
            RoleSourcePage(content, state, context, page));

        static IResult RoleSourcePage(
            ITabletContent content,
            TabletSpikeState state,
            HttpContext context,
            int page)
        {
            var role = SessionRole(state, context);
            if (role is null || page < 1 || page > content.RoleBooks[role.RoleId].SourcePageAssetIds.Count)
            {
                return Results.NotFound();
            }

            return content.TryGetRoleSourcePage(role.RoleId, page, out var bytes, out var contentType)
                ? Results.File(bytes, contentType)
                : Results.NotFound();
        }

        // 平板发言：由平板服务处理合法性与频次校验，转发给主持宿主。
        app.MapPost("/tablet/say", async (TabletSpeechService service, HttpContext context) =>
        {
            SayRequest? request = null;
            try
            {
                request = await context.Request.ReadFromJsonAsync<SayRequest>();
            }
            catch
            {
                // body 缺失或不是 JSON → 当作 Text = null
            }

            var result = await service.SayAsync(TabletSessions.KeyFor(context), request?.Text);
            return Results.Json(
                new
                {
                    ok = result.Code is TabletSayCode.Accepted or TabletSayCode.Proposed,
                    code = result.CodeText,
                    message = result.Message,
                },
                statusCode: result.HttpStatus);
        });

        // 停止主持朗读。
        app.MapPost("/tablet/say/stop", (TabletSpeechService service, HttpContext context) =>
        {
            var stopped = service.Stop(TabletSessions.KeyFor(context), out var failure);
            if (failure is TabletSayCode.NoSessionKey)
            {
                return Results.Json(
                    new { ok = false, code = "NO_SESSION_KEY", message = "请先从电脑上的二维码或网址重新打开本页。" },
                    statusCode: 401);
            }

            if (failure is TabletSayCode.NoRole)
            {
                return Results.Json(
                    new { ok = false, code = "NO_ROLE", message = "请先认领角色" },
                    statusCode: 403);
            }

            return Results.Json(new { stopped });
        });

        // 公开消息：按序号取。断线重连时带着上次的序号来要，就只拿新的。
        // 没有浏览器会话就拒绝：平板必须先加入这一局，不能凭猜来读。
        app.MapGet("/tablet/public", async (IPublicFeedSource source, TabletSpeechService speechService, HttpContext context) =>
        {
            if (TabletSessions.KeyFor(context) is null)
            {
                return Results.Unauthorized();
            }

            if (!TryParseAfter(context, out var after))
            {
                return Results.BadRequest();
            }

            var snapshot = await source.ReadAsync(after, context.RequestAborted);
            return Results.Json(new
            {
                sessionId = snapshot.SessionId,
                phase = new { id = snapshot.PhaseId, title = snapshot.PhaseTitle },
                messages = snapshot.Messages.Select(m => new
                {
                    sequence = m.Sequence,
                    messageId = m.MessageId,
                    author = m.Author.ToString(),
                    kind = m.Kind.ToString(),
                    text = m.Text,
                    createdAt = m.CreatedAt.ToString("O"),
                }),
                nextSequence = snapshot.NextSequence,
                host = new { busy = speechService.HostBusy, speaking = speechService.HostSpeaking },
            });
        });

        app.MapGet("/tablet/ui", async (ITabletUiSource ui, TabletSpeechService speechService, HttpContext context) =>
        {
            if (TabletSessions.KeyFor(context) is null)
            {
                return Results.Unauthorized();
            }

            if (!TryParseAfter(context, out var after))
            {
                return Results.BadRequest();
            }

            var projection = await ui.ReadAsync(after, speechService.HostBusy, speechService.HostSpeaking, context.RequestAborted);
            if (projection is null)
            {
                return Results.Json(new { ok = false, code = "NOT_READY", message = "等待主持人开始这一局" }, statusCode: 404);
            }

            return Results.Json(projection, UiJson);
        });

        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        return app;

        /// <summary>从 Cookie 解析本人角色；没有会话或没有认领时返回 null。</summary>
        static TabletRole? SessionRole(TabletSpikeState state, HttpContext context)
        {
            var key = TabletSessions.KeyFor(context);
            if (key is null)
            {
                return null;
            }

            var roleId = state.Session(key).RoleId;
            return roleId is null ? null : state.Roles.FirstOrDefault(r => r.RoleId == roleId);
        }
    }

    private static bool TryParseAfter(HttpContext context, out long after)
    {
        after = 0;
        if (!context.Request.Query.ContainsKey("after")) return true;
        return long.TryParse(context.Request.Query["after"], out after) && after >= 0;
    }

    private sealed record SayRequest(string? Text);
}
