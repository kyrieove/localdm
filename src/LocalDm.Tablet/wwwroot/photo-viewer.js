// 平板原稿和线索卡的全屏查看器，交互来自 docs/preview/fullscreen-viewer.html
(function () {
    "use strict";

    const clamp = (v, a, b) => Math.min(b, Math.max(a, v));

    let fs = null;
    let stage = null;
    let track = null;
    let titleEl = null;
    let closeBtn = null;
    let prevBtn = null;
    let nextBtn = null;
    let bannerEl = null;
    let toastEl = null;
    let bannerTimer = null;
    let toastTimer = null;

    let dotnet = null;
    const V = { open: false, list: [], i: 0, single: false, s: 1, tx: 0, ty: 0, dx: 0 };
    const W = () => (stage ? stage.clientWidth : 0);
    const curImg = () => {
        if (!track || !track.children[V.i]) return null;
        return track.children[V.i].querySelector("img");
    };

    function ensureDom() {
        if (fs) {
            // 返回键触发 Blazor 的页面合并，会把挂在 body 上的查看器摘掉；节点和监听都还在，挂回去即可
            if (!fs.isConnected) document.body.appendChild(fs);
            return;
        }
        fs = document.createElement("div");
        fs.id = "pv-fs";
        fs.className = "pv-fs";
        fs.hidden = true;

        fs.innerHTML = [
            '<div id="pv-stage" class="pv-stage"><div id="pv-track" class="pv-track"></div></div>',
            '<div class="pv-top">',
            '  <span id="pv-title" class="pv-title"></span>',
            '  <button id="pv-close" class="pv-close" type="button" aria-label="关闭">✕</button>',
            '</div>',
            '<button id="pv-prev" class="pv-arrow pv-prev" type="button" aria-label="上一页">‹</button>',
            '<button id="pv-next" class="pv-arrow pv-next" type="button" aria-label="下一页">›</button>',
            '<div id="pv-banner" class="pv-banner" hidden></div>',
            '<div id="pv-toast" class="pv-toast" hidden></div>'
        ].join("");

        document.body.appendChild(fs);

        stage = fs.querySelector("#pv-stage");
        track = fs.querySelector("#pv-track");
        titleEl = fs.querySelector("#pv-title");
        closeBtn = fs.querySelector("#pv-close");
        prevBtn = fs.querySelector("#pv-prev");
        nextBtn = fs.querySelector("#pv-next");
        bannerEl = fs.querySelector("#pv-banner");
        toastEl = fs.querySelector("#pv-toast");

        closeBtn.onclick = requestClose;
        prevBtn.onclick = () => go(-1);
        nextBtn.onclick = () => go(1);
        bannerEl.onclick = requestClose;

        window.addEventListener("popstate", () => {
            if (V.open) closeUi();
        });

        window.addEventListener("resize", () => {
            if (V.open) {
                applyTrack(false);
                applyZoom(false);
            }
        });

        window.addEventListener("keydown", e => {
            if (!V.open) return;
            if (e.key === "Escape") requestClose();
            else if (e.key === "ArrowLeft") go(-1);
            else if (e.key === "ArrowRight") go(1);
        });

        attachGestures();
    }

    function applyTrack(anim) {
        if (!track) return;
        track.style.transition = anim ? "transform .28s ease" : "none";
        track.style.transform = `translateX(${-V.i * W() + V.dx}px)`;
    }

    function maxPan() {
        const img = curImg();
        if (!img || !stage) return { mx: 0, my: 0 };
        const r = stage.getBoundingClientRect();
        const nw = img.naturalWidth || r.width;
        const nh = img.naturalHeight || r.height;
        const fit = Math.min(r.width / nw, r.height / nh);
        const mx = Math.max(0, (nw * fit * V.s - r.width) / 2);
        const my = Math.max(0, (nh * fit * V.s - r.height) / 2);
        return { mx, my };
    }

    function clampPan() {
        const { mx, my } = maxPan();
        V.tx = clamp(V.tx, -mx, mx);
        V.ty = clamp(V.ty, -my, my);
    }

    function applyZoom(anim) {
        clampPan();
        const img = curImg();
        if (!img) return;
        img.style.transition = anim ? "transform .2s ease" : "none";
        img.style.transform = `translate(${V.tx}px, ${V.ty}px) scale(${V.s})`;
    }

    function resetZoom() {
        V.s = 1;
        V.tx = 0;
        V.ty = 0;
    }

    function zoomAt(x, y, s, anim) {
        const r = stage.getBoundingClientRect();
        const cx = r.left + r.width / 2;
        const cy = r.top + r.height / 2;
        const qx = (x - cx - V.tx) / V.s;
        const qy = (y - cy - V.ty) / V.s;
        V.tx = x - cx - s * qx;
        V.ty = y - cy - s * qy;
        V.s = s;
        applyZoom(anim);
    }

    function showToast(text) {
        if (!toastEl) return;
        if (toastTimer) clearTimeout(toastTimer);
        toastEl.textContent = text;
        toastEl.hidden = false;
        toastTimer = setTimeout(() => {
            toastEl.hidden = true;
            toastTimer = null;
        }, 1500);
    }

    function updateUi() {
        if (!titleEl) return;
        titleEl.textContent = V.single ? "线索卡" : `第 ${V.i + 1} / ${V.list.length} 页`;
        prevBtn.hidden = nextBtn.hidden = V.single;
        prevBtn.disabled = V.i === 0;
        nextBtn.disabled = V.i === V.list.length - 1;

        preloadNearby();
    }

    function preloadNearby() {
        if (!track) return;
        const minIdx = Math.max(0, V.i - 1);
        const maxIdx = Math.min(V.list.length - 1, V.i + 1);
        for (let idx = minIdx; idx <= maxIdx; idx++) {
            const slide = track.children[idx];
            if (!slide) continue;
            const img = slide.querySelector("img");
            if (img && !img.src && img.dataset.src) {
                img.src = img.dataset.src;
            }
        }
    }

    function go(d) {
        const j = V.i + d;
        if (V.single || j < 0 || j >= V.list.length) {
            V.dx = 0;
            applyTrack(true);
            if (!V.single) {
                if (j < 0) showToast("已是第一页");
                else if (j >= V.list.length) showToast("已是最后一页");
            }
            return;
        }

        const oldImg = curImg();
        if (oldImg) {
            oldImg.style.transform = "";
            oldImg.style.transition = "none";
        }

        V.i = j;
        V.dx = 0;

        // 保留放大倍数，新的一页停在左上角：正值平移将左上角移入视野
        const { mx, my } = maxPan();
        V.tx = mx;
        V.ty = my;

        applyTrack(true);
        applyZoom(false);
        updateUi();
    }

    let tapTimer = null;
    let lastTap = null;

    function attachGestures() {
        const ptrs = new Map();
        let g = null;
        const mid = () => {
            const [a, b] = [...ptrs.values()];
            return {
                x: (a.x + b.x) / 2,
                y: (a.y + b.y) / 2,
                d: Math.hypot(a.x - b.x, a.y - b.y) || 1
            };
        };

        stage.addEventListener("pointerdown", e => {
            stage.setPointerCapture(e.pointerId);
            ptrs.set(e.pointerId, { x: e.clientX, y: e.clientY });
            if (ptrs.size === 1) {
                g = { mode: "", sx: e.clientX, sy: e.clientY, tx0: V.tx, ty0: V.ty, t: Date.now(), moved: false };
                V.dx = 0;
                applyTrack(false);
            } else if (ptrs.size === 2) {
                const m = mid();
                g = { mode: "pinch", m0: m, d0: m.d, s0: V.s, tx0: V.tx, ty0: V.ty, moved: true };
                V.dx = 0;
                applyTrack(false);
            }
        });

        stage.addEventListener("pointermove", e => {
            if (!ptrs.has(e.pointerId)) return;
            ptrs.set(e.pointerId, { x: e.clientX, y: e.clientY });
            if (!g) return;

            if (g.mode === "pinch" && ptrs.size >= 2) {
                const m = mid();
                const r = stage.getBoundingClientRect();
                const cx = r.left + r.width / 2;
                const cy = r.top + r.height / 2;
                const s = clamp(g.s0 * m.d / g.d0, 1, 5);
                const qx = (g.m0.x - cx - g.tx0) / g.s0;
                const qy = (g.m0.y - cy - g.ty0) / g.s0;
                V.s = s;
                V.tx = m.x - cx - s * qx;
                V.ty = m.y - cy - s * qy;
                applyZoom(false);
                return;
            }

            if (ptrs.size !== 1) return;
            const dx = e.clientX - g.sx;
            const dy = e.clientY - g.sy;
            if (!g.moved && Math.hypot(dx, dy) < 6) return;
            g.moved = true;

            if (!g.mode) {
                g.mode = V.s > 1.01 ? "pan" : (Math.abs(dx) >= Math.abs(dy) ? "swipe" : "none");
            }

            if (g.mode === "pan") {
                V.tx = g.tx0 + dx;
                V.ty = g.ty0 + dy;
                applyZoom(false);
            } else if (g.mode === "swipe") {
                const edge = (V.i === 0 && dx > 0) || (V.i === V.list.length - 1 && dx < 0);
                V.dx = V.single ? dx / 4 : (edge ? dx / 3 : dx);
                applyTrack(false);
            }
        });

        function endPointer(e) {
            if (!ptrs.delete(e.pointerId) || !g) return;

            if (g.mode === "pinch") {
                if (V.s < 1.05) {
                    resetZoom();
                    applyZoom(true);
                }
                if (ptrs.size === 1) {
                    const p = [...ptrs.values()][0];
                    g = { mode: "pan", sx: p.x, sy: p.y, tx0: V.tx, ty0: V.ty, moved: true };
                } else if (ptrs.size === 0) {
                    g = null;
                }
                return;
            }

            if (ptrs.size) return;

            if (g.mode === "swipe") {
                const dx = e.clientX - g.sx;
                const fast = Date.now() - g.t < 300 && Math.abs(dx) > 40;
                if (e.type === "pointerup" && (Math.abs(dx) > W() * 0.18 || fast)) {
                    go(dx < 0 ? 1 : -1);
                } else {
                    V.dx = 0;
                    applyTrack(true);
                }
            } else if (!g.moved && e.type === "pointerup") {
                const now = Date.now();
                const clientX = e.clientX;
                const clientY = e.clientY;

                if (lastTap && (now - lastTap.t < 320) && Math.hypot(clientX - lastTap.x, clientY - lastTap.y) < 30) {
                    if (tapTimer) {
                        clearTimeout(tapTimer);
                        tapTimer = null;
                    }
                    lastTap = null;
                    if (V.s > 1.01) {
                        resetZoom();
                        applyZoom(true);
                    } else {
                        zoomAt(clientX, clientY, 2.5, true);
                    }
                } else {
                    lastTap = { t: now, x: clientX, y: clientY };
                    if (tapTimer) clearTimeout(tapTimer);
                    tapTimer = setTimeout(() => {
                        tapTimer = null;
                        lastTap = null;
                        if (!V.single) {
                            const r = stage.getBoundingClientRect();
                            const rx = (clientX - r.left) / (r.width || 1);
                            if (rx <= 0.2) go(-1);
                            else if (rx >= 0.8) go(1);
                        }
                    }, 320);
                }
            }
            g = null;
        }

        stage.addEventListener("pointerup", endPointer);
        stage.addEventListener("pointercancel", endPointer);

        stage.addEventListener("wheel", e => {
            e.preventDefault();
            const s = clamp(V.s * (e.deltaY < 0 ? 1.15 : 1 / 1.15), 1, 5);
            if (s < 1.02) {
                resetZoom();
                applyZoom(true);
            } else {
                zoomAt(e.clientX, e.clientY, s, false);
            }
        }, { passive: false });
    }

    let wakeLockSentinel = null;
    let wakeLockGeneration = 0;

    async function requestWakeLock() {
        if ("wakeLock" in navigator && window.isSecureContext) {
            const generation = ++wakeLockGeneration;
            try {
                const sentinel = await navigator.wakeLock.request("screen");
                // 请求还没回来查看器就关了：拿到的锁立刻还掉，不让屏幕一直亮着
                if (generation !== wakeLockGeneration || !V.open) {
                    sentinel.release().catch(() => {});
                    return;
                }
                wakeLockSentinel = sentinel;
            } catch (_) {}
        }
    }

    function releaseWakeLock() {
        wakeLockGeneration++;
        if (wakeLockSentinel) {
            try {
                wakeLockSentinel.release();
            } catch (_) {}
            wakeLockSentinel = null;
        }
    }

    function open(urls, index, single, dotnetRef) {
        ensureDom();
        dotnet = dotnetRef || null;
        const list = Array.isArray(urls) ? urls : [];
        const initialIndex = clamp(index || 0, 0, Math.max(0, list.length - 1));

        Object.assign(V, {
            open: true,
            list,
            i: initialIndex,
            single: !!single,
            dx: 0
        });
        resetZoom();

        track.innerHTML = list.map((u, k) => {
            const alt = single ? "线索卡" : `原稿第 ${k + 1} 页`;
            return [
                '<div class="pv-slide">',
                '  <span class="pv-loading">加载中…</span>',
                `  <img data-src="${u}" alt="${alt}" draggable="false">`,
                '</div>'
            ].join("");
        }).join("");

        track.querySelectorAll(".pv-slide").forEach(slide => {
            const img = slide.querySelector("img");
            const loading = slide.querySelector(".pv-loading");
            if (img && loading) {
                img.addEventListener("load", () => {
                    loading.hidden = true;
                    // 放大翻到还没加载的页时，平移是按占位尺寸算的；图到了按真实尺寸重新停到左上角
                    if (V.open && curImg() === img && V.s > 1.01) {
                        const { mx, my } = maxPan();
                        V.tx = mx;
                        V.ty = my;
                        applyZoom(false);
                    }
                });
                img.addEventListener("error", () => { loading.hidden = true; });
            }
        });

        fs.hidden = false;
        document.body.style.overflow = "hidden";

        try {
            history.pushState({ tabletViewer: 1 }, "", location.href);
        } catch (_) {}

        if (document.fullscreenEnabled) {
            try {
                document.documentElement.requestFullscreen().catch(() => {});
            } catch (_) {}
        }

        requestWakeLock();

        applyTrack(false);
        updateUi();
        applyZoom(false);
    }

    function closeUi() {
        if (!V.open) return;
        V.open = false;
        if (fs) fs.hidden = true;
        document.body.style.overflow = "";

        if (document.fullscreenElement) {
            try {
                document.exitFullscreen().catch(() => {});
            } catch (_) {}
        }

        releaseWakeLock();

        if (bannerTimer) {
            clearTimeout(bannerTimer);
            bannerTimer = null;
        }
        if (toastTimer) {
            clearTimeout(toastTimer);
            toastTimer = null;
        }
        if (bannerEl) bannerEl.hidden = true;
        if (toastEl) toastEl.hidden = true;
        if (tapTimer) {
            clearTimeout(tapTimer);
            tapTimer = null;
        }
        lastTap = null;

        if (dotnet) {
            const closedPage = V.i + 1;
            dotnet.invokeMethodAsync("ViewerClosed", closedPage).catch(() => {});
            dotnet = null;
        }
    }

    function requestClose() {
        if (history.state && history.state.tabletViewer) {
            history.back();
        } else {
            closeUi();
        }
    }

    function close() {
        dotnet = null;
        requestClose();
    }

    function notify(text) {
        if (!V.open || !bannerEl) return;
        const raw = (text || "").toString();
        const content = raw.length > 40 ? raw.slice(0, 40) + "…" : raw;
        bannerEl.textContent = content;
        bannerEl.hidden = false;

        if (bannerTimer) clearTimeout(bannerTimer);
        bannerTimer = setTimeout(() => {
            bannerEl.hidden = true;
            bannerTimer = null;
        }, 6000);
    }

    window.tabletViewer = {
        open,
        close,
        notify
    };
})();
