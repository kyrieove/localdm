// 阅读器分页：按可见页面的实际布局试排，按 UTF-16 文本偏移恢复位置。
(function () {
  const state = {
    blocks: [],
    pages: [],
    starts: [],
    index: 0,
    body: null,
    measure: null,
    onChange: null,
    dotNetReference: null,
    resizeObserver: null,
    resizeTimer: null,
    resizeSize: null,
    generation: 0,
    paginateVersion: 0,
    lastDisplay: null,
    imageLoads: new Map(),
  };

  function figureUrl(block) {
    if (typeof block.sourceUrl !== 'string') return null;
    try {
      const url = new URL(block.sourceUrl, window.location.href);
      if (url.origin !== window.location.origin || !/^\/tablet\/role\/source\/\d+$/.test(url.pathname)) return null;
      return url.pathname;
    } catch {
      return null;
    }
  }

  function textOf(block) {
    if (block.kind === 'Break') return '';
    const text = typeof block.text === 'string' ? block.text : '';
    if (block.kind !== 'Figure') return text;
    return text + (typeof block.caption === 'string' ? block.caption : '');
  }

  function flatten(blocks) {
    let offset = 0;
    let chapter = -1;
    return blocks.map((block, index) => {
      if (block.kind === 'ChapterTitle') chapter++;
      const text = textOf(block);
      const entry = {
        block,
        index,
        offset,
        text,
        chapter: Math.max(0, chapter),
      };
      offset += text.length;
      return entry;
    });
  }

  function safePageStart(fragment) {
    return Number.isFinite(fragment?.startOffset) ? fragment.startOffset : 0;
  }

  function makeFragment(entry, start, end) {
    return {
      entry,
      text: entry.text.slice(start, end),
      consumed: start,
      startOffset: entry.offset + start,
    };
  }

  function codePointBoundaries(text) {
    const boundaries = [0];
    for (let offset = 0; offset < text.length;) {
      const codePoint = text.codePointAt(offset);
      offset += codePoint > 0xffff ? 2 : 1;
      boundaries.push(offset);
    }
    return boundaries;
  }

  function appendFigureText(caption, text, className) {
    if (!text) return;
    const span = document.createElement('span');
    span.className = className;
    span.textContent = text;
    caption.appendChild(span);
  }

  function createNode(fragment) {
    const { block, kind } = { block: fragment.entry.block, kind: fragment.entry.block.kind };
    let node;

    if (kind === 'Figure') {
      node = document.createElement('figure');
      node.className = 'reader-figure';
      const src = figureUrl(block);
      if (src) {
        const image = document.createElement('img');
        image.src = src;
        image.alt = block.caption || block.text || '角色本插图';
        image.loading = 'eager';
        node.appendChild(image);
      }
      if (block.text || block.caption) {
        const caption = document.createElement('figcaption');
        appendFigureText(caption, typeof block.text === 'string' ? block.text : '', 'figure-text');
        appendFigureText(caption, typeof block.caption === 'string' ? block.caption : '', 'figure-caption');
        node.appendChild(caption);
      }
      return node;
    }

    if (kind === 'Break') {
      node = document.createElement('hr');
      node.className = 'reader-break';
      node.setAttribute('aria-hidden', 'true');
      return node;
    }

    if (kind === 'ChapterTitle' || kind === 'SectionTitle') {
      node = document.createElement('h2');
      node.className = kind === 'ChapterTitle' ? 'chapter' : 'section';
      node.textContent = fragment.text;
      return node;
    }

    node = document.createElement('p');
    if (kind === 'Emphasis') {
      node.className = 'emphasis';
      const emphasis = document.createElement('em');
      emphasis.textContent = fragment.text;
      node.appendChild(emphasis);
    } else {
      node.className = 'para';
      node.textContent = fragment.text;
    }
    if (fragment.consumed > 0) node.classList.add('continuation');
    return node;
  }

  function renderFragments(container, fragments) {
    const content = document.createDocumentFragment();
    for (const fragment of fragments) content.appendChild(createNode(fragment));
    container.replaceChildren(content);
  }

  function fits(page, fragment) {
    renderFragments(state.measure, [...page, fragment]);
    const available = state.measure.clientHeight;
    return available > 0 && state.measure.scrollHeight <= available + 1;
  }

  function finishPage(pages, starts, page) {
    if (!page.length) return;
    pages.push(page);
    starts.push(safePageStart(page[0]));
  }

  function hasText(page) {
    return page.some((fragment) => fragment.entry.text.length > 0);
  }

  function canSplit(block) {
    return block.kind !== 'ChapterTitle'
      && block.kind !== 'SectionTitle'
      && block.kind !== 'Figure'
      && block.kind !== 'Break';
  }

  function buildPages(entries) {
    const pages = [];
    const starts = [];
    let page = [];

    function nextPage() {
      finishPage(pages, starts, page);
      page = [];
    }

    for (const entry of entries) {
      const { kind } = entry.block;
      if (kind === 'ChapterTitle' && page.length) {
        if (!hasText(page) && pages.length) {
          // 没有文本偏移的尾随插图/分隔留在上一页，避免和新章页产生相同锚点。
          pages[pages.length - 1].push(...page);
          page = [];
        } else if (hasText(page)) {
          nextPage();
        }
      }

      if (!canSplit(entry.block)) {
        const fragment = {
          entry,
          text: entry.text,
          consumed: 0,
          startOffset: entry.offset,
        };
        if (page.length && !fits(page, fragment) && hasText(page)) nextPage();
        // 一个标题、图片或分隔本身高于可用高度时，留在当前页并由可见区域滚动。
        page.push(fragment);
        continue;
      }

      const boundaries = codePointBoundaries(entry.text);
      let consumedPoints = 0;
      const pointCount = boundaries.length - 1;

      if (pointCount === 0) {
        const fragment = makeFragment(entry, 0, 0);
        if (page.length && !fits(page, fragment)) nextPage();
        page.push(fragment);
        continue;
      }

      while (consumedPoints < pointCount) {
        const start = boundaries[consumedPoints];
        let low = 0;
        let high = pointCount - consumedPoints;
        while (low < high) {
          const middle = Math.ceil((low + high) / 2);
          const end = boundaries[consumedPoints + middle];
          const candidate = makeFragment(entry, start, end);
          if (fits(page, candidate)) low = middle;
          else high = middle - 1;
        }

        if (low === 0) {
          if (page.length) {
            if (hasText(page)) {
              nextPage();
              continue;
            }
            // 无文本的图片/分隔无法形成独立恢复位置，和后续文字共用可滚动页。
            page.push(makeFragment(entry, start, entry.text.length));
            consumedPoints = pointCount;
            break;
          }
          // 即使视口过矮，也保留整段并允许滚动，不能静默丢掉剩余文字。
          page.push(makeFragment(entry, start, entry.text.length));
          consumedPoints = pointCount;
          break;
        }

        const end = boundaries[consumedPoints + low];
        page.push(makeFragment(entry, start, end));
        consumedPoints += low;
        if (consumedPoints < pointCount) nextPage();
      }
    }

    finishPage(pages, starts, page);
    return { pages, starts };
  }

  function displaySignature() {
    const page = state.pages[state.index] || [];
    const fragments = page.map((fragment) => {
      const block = fragment.entry.block;
      return `${fragment.entry.index}:${fragment.startOffset}:${fragment.text.length}:${figureUrl(block) || ''}`;
    });
    return JSON.stringify([state.index, state.pages.length, state.starts[state.index] ?? 0, fragments]);
  }

  function notifyChange() {
    const signature = displaySignature();
    if (signature === state.lastDisplay) return;
    state.lastDisplay = signature;
    if (state.onChange) {
      state.onChange(state.index, state.pages.length, state.starts[state.index] ?? 0);
    }
  }

  function render() {
    if (!state.body) return;
    const page = state.pages[state.index] || [];
    renderFragments(state.body, page);
    notifyChange();
  }

  function loadImage(url) {
    if (state.imageLoads.has(url)) return state.imageLoads.get(url);
    const promise = new Promise((resolve) => {
      const image = new Image();
      let complete = false;
      const finish = () => {
        if (complete) return;
        complete = true;
        clearTimeout(timeout);
        resolve();
      };
      const timeout = setTimeout(finish, 3000);
      image.onload = finish;
      image.onerror = finish;
      image.src = url;
      if (image.complete) finish();
    });
    state.imageLoads.set(url, promise);
    return promise;
  }

  function pageOffset() {
    return state.starts[state.index] ?? 0;
  }

  function currentSize(element) {
    const rect = element.getBoundingClientRect();
    return { width: rect.width, height: rect.height };
  }

  function stopObserving() {
    if (state.resizeObserver) state.resizeObserver.disconnect();
    state.resizeObserver = null;
    if (state.resizeTimer !== null) clearTimeout(state.resizeTimer);
    state.resizeTimer = null;
    state.resizeSize = null;
  }

  function observeLayout() {
    const target = state.measure?.parentElement;
    if (!target || typeof ResizeObserver === 'undefined') return;
    state.resizeSize = currentSize(target);
    state.resizeObserver = new ResizeObserver((entries) => {
      const rect = entries[0]?.contentRect;
      if (!rect) return;
      const previous = state.resizeSize;
      state.resizeSize = { width: rect.width, height: rect.height };
      if (previous && Math.abs(previous.width - rect.width) < 1 && Math.abs(previous.height - rect.height) < 1) return;
      if (!state.pages.length) return;

      if (state.resizeTimer !== null) clearTimeout(state.resizeTimer);
      const generation = state.generation;
      state.resizeTimer = setTimeout(async () => {
        state.resizeTimer = null;
        if (generation !== state.generation) return;
        const before = state.lastDisplay;
        await window.tabletReader.paginate(pageOffset());
        if (generation !== state.generation || before === state.lastDisplay) return;
        try {
          await state.dotNetReference?.invokeMethodAsync('ReaderLayoutChanged');
        } catch {
          // Blazor circuit may have disconnected while the resize was being handled.
        }
      }, 140);
    });
    state.resizeObserver.observe(target);
  }

  window.tabletReader = {
    mount(body, measure, blocks, dotNetReference) {
      stopObserving();
      state.generation++;
      state.paginateVersion++;
      state.body = body;
      state.measure = measure;
      state.blocks = Array.isArray(blocks) ? blocks : [];
      state.pages = [];
      state.starts = [];
      state.index = 0;
      state.dotNetReference = dotNetReference || null;
      state.lastDisplay = null;
      state.imageLoads.clear();
      observeLayout();
    },

    reset() {
      stopObserving();
      state.generation++;
      state.paginateVersion++;
      state.body = null;
      state.measure = null;
      state.blocks = [];
      state.pages = [];
      state.starts = [];
      state.index = 0;
      state.dotNetReference = null;
      state.onChange = null;
      state.lastDisplay = null;
      state.imageLoads.clear();
    },

    async paginate(anchor) {
      const generation = state.generation;
      const version = ++state.paginateVersion;
      const entries = flatten(state.blocks);
      const urls = [...new Set(entries.map((entry) => figureUrl(entry.block)).filter(Boolean))];
      await Promise.all(urls.map(loadImage));
      if (generation !== state.generation || version !== state.paginateVersion || !state.measure) return;

      const built = buildPages(entries);
      state.pages = built.pages;
      state.starts = built.starts;
      const position = Number.isFinite(Number(anchor)) ? Math.max(0, Number(anchor)) : 0;
      let next = 0;
      for (let i = 0; i < state.starts.length; i++) {
        if (state.starts[i] <= position) next = i;
        else break;
      }
      state.index = next;
      render();
    },

    turn(delta) {
      const next = state.index + delta;
      if (next < 0 || next >= state.pages.length) return;
      state.paginateVersion++;
      state.index = next;
      render();
    },

    jumpChapter(index) {
      const chapter = Number(index);
      if (!Number.isInteger(chapter) || chapter < 0) return;
      const pageIndex = state.pages.findIndex((page) => page.some((fragment) => fragment.entry.chapter === chapter));
      if (pageIndex < 0) return;
      state.paginateVersion++;
      state.index = pageIndex;
      render();
    },

    total() { return state.pages.length; },
    index() { return state.index; },
    offset() { return pageOffset(); },

    chapter() {
      return state.pages[state.index]?.[0]?.entry.chapter ?? 0;
    },

    sourcePage() {
      const page = state.pages[state.index] || [];
      for (const fragment of page) {
        const number = Number(fragment.entry.block.sourcePageNumber);
        if (Number.isInteger(number) && number > 0) return number;
      }
      return null;
    },

    onChange(handler) {
      state.onChange = typeof handler === 'function' ? handler : null;
    },

    flatText() {
      return flatten(state.blocks).map((entry) => entry.text).join('');
    },

    paginatedText() {
      return state.pages
        .flat()
        .map((fragment) => fragment.entry.block.kind === 'Break' ? '' : fragment.text)
        .join('');
    },
  };
})();
