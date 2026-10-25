// 角色本翻页：按住约 0.65 秒、圆环走满才翻开，避免手滑多翻一页。计时在浏览器里做，服务端渲染的往返延迟不影响手感。
(() => {
    const HOLD_MS = 650;
    const FULL = 251;
    let dotnet = null;

    document.addEventListener("pointerdown", event => {
        const back = event.target.closest("[data-flip-page]");
        if (!back || !dotnet) return;
        const fill = back.querySelector(".fill");
        const page = Number(back.dataset.flipPage);
        const started = performance.now();
        let frame = 0;
        let done = false;
        const tick = now => {
            if (fill) fill.style.strokeDashoffset = FULL * (1 - Math.min(1, (now - started) / HOLD_MS));
            frame = requestAnimationFrame(tick);
        };
        const stop = () => {
            clearTimeout(timer);
            cancelAnimationFrame(frame);
            if (!done && fill) fill.style.strokeDashoffset = FULL;
            ["pointerup", "pointercancel", "pointerleave"].forEach(type => back.removeEventListener(type, stop));
        };
        const timer = setTimeout(() => {
            done = true;
            stop();
            if (navigator.vibrate) navigator.vibrate(12);
            dotnet.invokeMethodAsync("HoldOpen", page);
        }, HOLD_MS);
        frame = requestAnimationFrame(tick);
        ["pointerup", "pointercancel", "pointerleave"].forEach(type => back.addEventListener(type, stop));
    });

    // 长按时别弹出系统菜单。
    document.addEventListener("contextmenu", event => {
        if (event.target.closest("[data-flip-page]")) event.preventDefault();
    });

    window.tabletFlip = {
        attach: reference => { dotnet = reference; },
        detach: () => { dotnet = null; },
    };
})();
