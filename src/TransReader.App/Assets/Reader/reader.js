(() => {
  const $ = id => document.getElementById(id);
  const content = $('content'), toolbar = $('selectionToolbar');
  const translationView = $('translationView'), assistantView = $('assistantView'), conversation = $('conversation');
  const md = window.markdownit({ html: false, linkify: true, typographer: false, breaks: false });
  let pageNumber = 0, selection = null, activeTopic = '', topics = [], mode = 'translation';
  let translationScroll = 0, assistantScroll = 0;
  // 流式渲染状态：上次已渲染的完整 markdown、上次是否为最终态。换页/清空时重置。
  let lastMarkdown = '', prevIsFinal = false;
  const post = (type, extra = {}) => window.chrome.webview.postMessage({ type, ...extra });

  // 把完整 \[...\] 与 \(...\) 抽成占位符，未闭合定界符降级为字面量，避免流式中途炸渲染。
  // 占位符用 Unicode 私用区字符包裹（fromCharCode 构造，源码不出现不可见字符），与可读文本零冲突。
  const MATH_OPEN = String.fromCharCode(0xE000), MATH_CLOSE = String.fromCharCode(0xE001);
  const MATH_PATTERN = new RegExp(`${MATH_OPEN}(\\d+)${MATH_CLOSE}|${MATH_OPEN}(lp|rp|lb|rb)${MATH_CLOSE}`, 'g');
  const protectMath = source => {
    const formulas = [];
    // 除契约约定的 \(...\) \[...\] 外，兼容模型实际爱用的 $...$ $$...$$（pandoc 规则：
    // 开 $ 后非空白、闭 $ 前非空白，避免“$5 与 $6”这类金额被误吞）。
    let value = source.replace(
      /\\\[([\s\S]*?)\\\]|\\\(([\s\S]*?)\\\)|\$\$([\s\S]+?)\$\$|\$([^\s$][^$\n]*?[^\s$]|[^\s$])\$/g,
      (all, display, inline, doubleDollar, singleDollar) => {
        const tex = display ?? inline ?? doubleDollar ?? singleDollar;
        const isDisplay = display !== undefined || doubleDollar !== undefined;
        const index = formulas.push({ tex, display: isDisplay }) - 1;
        return `${MATH_OPEN}${index}${MATH_CLOSE}`;
      });
    value = value.replace(/\\\(/g, `${MATH_OPEN}lp${MATH_CLOSE}`)
      .replace(/\\\)/g, `${MATH_OPEN}rp${MATH_CLOSE}`)
      .replace(/\\\[/g, `${MATH_OPEN}lb${MATH_CLOSE}`)
      .replace(/\\\]/g, `${MATH_OPEN}rb${MATH_CLOSE}`);
    return { value, formulas };
  };

  // 将 markdown 渲染成一组 DOM 节点（含 KaTeX 占位符回填）。可整体替换或增量追加。
  const renderNodes = markdown => {
    const protectedMath = protectMath(markdown || '');
    const container = document.createElement('div');
    container.innerHTML = md.render(protectedMath.value);
    const walker = document.createTreeWalker(container, NodeFilter.SHOW_TEXT);
    const nodes = [];
    while (walker.nextNode()) nodes.push(walker.currentNode);
    for (const node of nodes) {
      const pattern = MATH_PATTERN;
      if (!pattern.test(node.nodeValue)) continue;
      pattern.lastIndex = 0;
      const fragment = document.createDocumentFragment();
      let last = 0, match;
      while ((match = pattern.exec(node.nodeValue))) {
        fragment.append(document.createTextNode(node.nodeValue.slice(last, match.index)));
        if (match[1] !== undefined) {
          const formula = protectedMath.formulas[Number(match[1])];
          if (formula) {
            const span = document.createElement(formula.display ? 'div' : 'span');
            try { katex.render(formula.tex, span, { displayMode: formula.display, throwOnError: false, strict: false }); }
            catch (_) { span.textContent = formula.display ? `\\[${formula.tex}\\]` : `\\(${formula.tex}\\)`; }
            fragment.append(span);
          } else {
            // 索引越界（异常输入）：静默丢弃占位符，绝不以"豆腐块"示人、绝不中断渲染。
          }
        } else {
          fragment.append(document.createTextNode({ lp: '\\(', rp: '\\)', lb: '\\[', rb: '\\]' }[match[2]]));
        }
        last = pattern.lastIndex;
      }
      fragment.append(document.createTextNode(node.nodeValue.slice(last)));
      node.replaceWith(fragment);
    }
    // 兜底：任何未被还原的私用区字符（异常输入、模型幻觉出的伪占位符）一律抹除，
    // 包括被切散的 \uE000…\uE001 残段，绝不以豆腐块示人。
    const sweep = document.createTreeWalker(container, NodeFilter.SHOW_TEXT);
    const leftovers = [];
    while (sweep.nextNode()) leftovers.push(sweep.currentNode);
    const leftoverPattern = new RegExp(`${MATH_OPEN}[^${MATH_CLOSE}]*${MATH_CLOSE}|[\uE000-\uF8FF]`, 'g');
    for (const node of leftovers) {
      if (leftoverPattern.test(node.nodeValue)) {
        node.nodeValue = node.nodeValue.replace(leftoverPattern, '');
      }
    }
    return container;
  };

  const renderInto = (element, markdown) => {
    const rendered = renderNodes(markdown);
    element.replaceChildren(...rendered.childNodes);
  };

  const wrapTables = element => {
    for (const table of element.querySelectorAll('table')) {
      if (table.parentElement?.classList.contains('table-scroll')) continue;
      const wrapper = document.createElement('div');
      wrapper.className = 'table-scroll';
      table.replaceWith(wrapper);
      wrapper.append(table);
    }
  };

  // 选取视口顶部首个可见块作为滚动锚点（用其文本签名 + 原顶边偏移），重建后按签名找回。
  const captureAnchor = () => {
    for (const child of content.children) {
      const rect = child.getBoundingClientRect();
      if (rect.bottom <= 0) continue;
      const text = (child.textContent || '').trim().slice(0, 40);
      if (text) return { text, offset: rect.top };
    }
    return null;
  };
  const restoreAnchor = anchor => {
    if (!anchor) return;
    for (const child of content.children) {
      if ((child.textContent || '').trim().startsWith(anchor.text)) {
        const delta = child.getBoundingClientRect().top - anchor.offset;
        if (delta) translationView.scrollTop += delta;
        return;
      }
    }
  };

  const renderPage = update => {
    toolbar.hidden = true;
    const markdown = update.markdown || '';
    if (markdown === lastMarkdown) return; // 内容未变，跳过重建与重排
    try {
      const nearBottom = translationView.scrollHeight - translationView.scrollTop - translationView.clientHeight < 48;
      const anchor = captureAnchor();
      document.body.classList.toggle('final', !!update.isFinal);
      document.body.classList.toggle('updating', !update.isFinal);
      // 增量追加：仅当新 markdown 严格扩展上一版、且切点落在空行段落边界时才追加尾部，
      // 否则一律全量重建（避免把同一段落错误拆成两段）。
      const isPrefix = lastMarkdown.length > 0 && markdown.length > lastMarkdown.length
        && markdown.startsWith(lastMarkdown) && /\n\n$/.test(lastMarkdown);
      if (isPrefix) {
        const tail = renderNodes(markdown.slice(lastMarkdown.length));
        content.append(...tail.childNodes);
        wrapTables(content);
      } else {
        renderInto(content, markdown);
        wrapTables(content);
      }
      lastMarkdown = markdown;
      if (update.autoFollow && nearBottom) {
        translationView.scrollTop = translationView.scrollHeight;
      } else if (!isPrefix) {
        restoreAnchor(anchor); // 增量追加不会移动上方内容，无需锚点恢复
      }
      if (update.isFinal && !prevIsFinal && content.animate) {
        content.animate([{ opacity: 0.55 }, { opacity: 1 }], { duration: 150, easing: 'ease-out' });
      }
      prevIsFinal = !!update.isFinal;
    } catch (error) {
      // 渲染异常时降级为纯文本：译文始终可见，绝不冻结在旧版本；下次更新会自动恢复富渲染。
      console.error('renderPage failed', error);
      content.textContent = markdown;
      lastMarkdown = markdown;
      prevIsFinal = !!update.isFinal;
    }
  };

  const structureFor = node => {
    const parent = node?.nodeType === 3 ? node.parentElement : node;
    if (parent?.closest('.katex,.katex-display')) return 'formula';
    const tag = parent?.closest('table,pre,code,blockquote,h1,h2,h3,h4,li')?.tagName?.toLowerCase();
    return tag || 'paragraph';
  };

  const captureSelection = () => {
    const rangeSelection = window.getSelection();
    if (!rangeSelection || rangeSelection.rangeCount === 0 || rangeSelection.isCollapsed || !content.contains(rangeSelection.anchorNode)) { toolbar.hidden = true; return; }
    const text = rangeSelection.toString().trim();
    if (!text) { toolbar.hidden = true; return; }
    const range = rangeSelection.getRangeAt(0);
    const container = range.commonAncestorContainer.nodeType === 3 ? range.commonAncestorContainer.parentElement : range.commonAncestorContainer;
    const semantic = container.closest?.('p,li,blockquote,td,th,h1,h2,h3,h4,pre') || container;
    selection = {
      selectedText: text,
      surroundingText: (semantic?.innerText || text).trim(),
      structureType: structureFor(range.commonAncestorContainer),
      pageNumber
    };
    const rect = range.getBoundingClientRect();
    toolbar.style.left = `${Math.max(8, Math.min(window.innerWidth - toolbar.offsetWidth - 8, rect.left + rect.width / 2 - 65))}px`;
    toolbar.style.top = `${Math.max(8, rect.top - 48)}px`;
    toolbar.hidden = false;
    post('selectionChanged', selection);
  };

  const setMode = value => {
    if (value === mode) return;
    if (mode === 'translation') translationScroll = translationView.scrollTop;
    else assistantScroll = conversation.scrollTop;
    mode = value;
    const assistant = value === 'assistant';
    translationView.hidden = assistant;
    assistantView.hidden = !assistant;
    toolbar.hidden = true;
    requestAnimationFrame(() => {
      if (assistant) conversation.scrollTop = assistantScroll;
      else translationView.scrollTop = translationScroll;
    });
  };

  const makeTopicButton = topic => {
    const button = document.createElement('button');
    button.className = `topic-item${topic.id === activeTopic ? ' active' : ''}`;
    button.type = 'button';
    button.title = `第 ${topic.pageNumber} 页 · ${topic.title}`;
    const page = document.createElement('span');
    page.className = 'topic-page';
    page.textContent = `P${topic.pageNumber}`;
    const title = document.createElement('span');
    title.className = 'topic-title';
    title.textContent = topic.title;
    button.append(page, title);
    button.onclick = () => post('openTopic', { topicId: topic.id });
    return button;
  };

  const renderTopics = () => {
    for (const id of ['topicList', 'mobileTopicList']) $(id).replaceChildren(...topics.map(makeTopicButton));
  };

  const setEmpty = empty => {
    $('assistantEmpty').hidden = !empty;
    $('selectionCard').hidden = empty;
    $('presetChips').hidden = empty;
    $('assistantPage').textContent = empty ? (pageNumber ? `第 ${pageNumber} 页` : '当前页面') : $('assistantPage').textContent;
  };

  const showTopic = topic => {
    activeTopic = topic.id;
    setMode('assistant');
    $('selectionQuote').textContent = topic.selectedText;
    $('assistantPage').textContent = `第 ${topic.pageNumber} 页`;
    $('selectionCard').hidden = false;
    conversation.replaceChildren();
    for (const message of topic.messages || []) {
      const item = document.createElement('div');
      item.className = `message ${message.role}`;
      renderInto(item, message.markdown);
      wrapTables(item);
      conversation.append(item);
    }
    setEmpty(false);
    $('assistantError').hidden = true;
    renderTopics();
    requestAnimationFrame(() => conversation.scrollTop = conversation.scrollHeight);
  };

  const updateAnswer = update => {
    if (update.topicId !== activeTopic) return;
    let item = conversation.querySelector('.message.assistant.pending');
    if (!item) {
      item = document.createElement('div');
      item.className = 'message assistant pending';
      conversation.append(item);
    }
    renderInto(item, update.markdown);
    wrapTables(item);
    item.classList.toggle('pending', !update.isFinal);
    $('stopAnswer').hidden = update.isFinal;
    setComposerBusy(!update.isFinal);
    requestAnimationFrame(() => conversation.scrollTop = conversation.scrollHeight);
  };

  const presetChips = [...document.querySelectorAll('#presetChips button')];
  // 生成答案期间统一禁用发送入口（发送按钮 + 预设问题 chips），恢复时一并解除。
  const setComposerBusy = busy => {
    $('sendQuestion').disabled = busy;
    for (const chip of presetChips) chip.disabled = busy;
  };
  $('explainButton').onclick = () => selection && post('explainSelection', selection);
  $('askButton').onclick = () => {
    if (selection) {
      post('askSelection', selection);
      setMode('assistant');
      setTimeout(() => $('questionInput').focus(), 0);
    }
  };
  $('stopAnswer').onclick = () => post('stopAnswer');
  // 发送追问：输入框发送与预设问题 chips 走同一路径。
  const sendFollowUpQuestion = question => {
    if (!question || !activeTopic) return;
    const item = document.createElement('div');
    item.className = 'message user';
    item.textContent = question;
    conversation.append(item);
    post('sendFollowUp', { topicId: activeTopic, question });
    $('questionInput').value = '';
    $('stopAnswer').hidden = false;
    setComposerBusy(true);
    requestAnimationFrame(() => conversation.scrollTop = conversation.scrollHeight);
  };
  $('sendQuestion').onclick = () => sendFollowUpQuestion($('questionInput').value.trim());
  for (const chip of presetChips) chip.onclick = () => sendFollowUpQuestion(chip.textContent.trim());
  $('questionInput').addEventListener('keydown', event => {
    if (event.key === 'Enter' && !event.shiftKey) { event.preventDefault(); $('sendQuestion').click(); }
  });
  document.addEventListener('mouseup', event => {
    if (mode === 'translation' && !toolbar.contains(event.target)) setTimeout(captureSelection, 0);
  });
  document.addEventListener('mousedown', event => { if (!toolbar.contains(event.target)) toolbar.hidden = true; });
  document.addEventListener('click', event => {
    const link = event.target.closest('a');
    if (!link) return;
    event.preventDefault();
    post('openLink', { url: link.href });
  });
  // WebView2 聚焦时其 HWND 会绕过 XAML 键盘加速器，导致翻页/快捷键静默失效；
  // 这里把阅读快捷键转发给宿主，与主窗口加速器保持一致。编辑控件（提问输入框）内放行。
  document.addEventListener('keydown', event => {
    const target = event.target;
    if (target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.isContentEditable)) return;
    const key = event.key;
    let modifiers = '';
    if (event.ctrlKey) modifiers += 'Ctrl';
    if (event.altKey) modifiers += 'Alt';
    if (event.shiftKey) modifiers += 'Shift';
    const navigation = modifiers === '' && ['ArrowLeft', 'ArrowRight', 'PageUp', 'PageDown', 'Home', 'End'].includes(key);
    const command = modifiers === 'Ctrl' && ['o', 'r', ','].includes(key.toLowerCase());
    if (!navigation && !command) return;
    event.preventDefault();
    post('keyDown', { key, modifiers });
  });

  window.transReader = {
    update: renderPage,
    theme: value => document.documentElement.dataset.theme = value === 'dark' ? 'dark' : 'light',
    clear: () => { content.textContent = ''; toolbar.hidden = true; lastMarkdown = ''; prevIsFinal = false; },
    setPage: value => { pageNumber = value; toolbar.hidden = true; lastMarkdown = ''; prevIsFinal = false; },
    setTopics: value => { topics = value || []; renderTopics(); },
    showTranslation: () => setMode('translation'),
    showAssistant: () => {
      setMode('assistant');
      if (!activeTopic) { setEmpty(true); conversation.replaceChildren(); renderTopics(); }
    },
    showTopic,
    updateAnswer,
    // 模型徽章：meta = { model: "Kimi K3 · 在线", local: false }；local 为 true 时追加“本地运行·不上传”。
    // 幂等：重复调用只覆盖同一徽章；model 缺失/为空时回落显示 “—”。
    setAssistantMeta: meta => {
      const badge = $('assistantModelBadge');
      const model = (meta && typeof meta.model === 'string' ? meta.model : '').trim();
      const local = model.length > 0 && !!meta.local;
      badge.textContent = model ? (local ? `${model} · 本地运行·不上传` : model) : '—';
      badge.title = model;
      badge.classList.toggle('local', local);
    },
    showAssistantError: message => {
      setMode('assistant');
      $('assistantError').textContent = message;
      $('assistantError').hidden = false;
      $('stopAnswer').hidden = true;
      setComposerBusy(false);
    }
  };
  post('ready');
})();
