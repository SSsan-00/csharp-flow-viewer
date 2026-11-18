(() => {
  const viewport = document.querySelector("#viewport");
  const graph = document.querySelector("#graph");
  const feedback = document.querySelector("#feedback");
  const menu = document.querySelector("#node-menu");
  const scroller = document.querySelector("#vertical-scroll");
  const search = document.querySelector("#preview-search");
  const query = search.querySelector("input");
  const matchCount = search.querySelector("output");
  let matches = [], matchIndex = -1, expectedScroll = 0;
  const host = window.chrome?.webview;
  const palettes = {
    dark: { background: "#101521", lineColor: "#b7bfd7", edgeLabelBackground: "#222a3b", textColor: "#e7e9f7", nodeTextColor: "#e7e9f7" },
    light: { background: "#f7f8fc", lineColor: "#43516b", edgeLabelBackground: "#ecf0fa", textColor: "#232b43", nodeTextColor: "#232b43" }
  };

  let width = 0, height = 0, x = 0, y = 0, scale = 1;
  let generation = 0, version = 0, drag, suppressClick;
  let menuNode, fittedRevision;
  let queue = Promise.resolve();
  const originalStyles = new WeakMap();
  const send = (type, value) => host?.postMessage({ type, value, version });
  const sendZoom = () => send("zoom", Number((scale * 100).toPrecision(3)));

  function apply() {
    graph.style.transform = `translate(${x}px, ${y}px) scale(${scale})`;
    syncScroll();
    send("viewport", { X: x, Y: y, Scale: scale });
  }

  function syncScroll() {
    const contentHeight = Math.ceil(height * scale + 36);
    scroller.hidden = !height || contentHeight <= viewport.clientHeight;
    if (scroller.hidden) return;
    scroller.firstElementChild.style.height = contentHeight + "px";
    scroller.scrollTop = 18 - y;
    expectedScroll = scroller.scrollTop;
  }

  scroller.addEventListener("scroll", () => {
    if (scroller.hidden || scroller.scrollTop === expectedScroll) return;
    y = 18 - scroller.scrollTop;
    apply();
  });
  new ResizeObserver(syncScroll).observe(viewport);
  function fit() {
    if (!width || !height) return;
    scale = Math.min(1, Math.max(.000001, (viewport.clientWidth - 52) / width),
      Math.max(.000001, (viewport.clientHeight - 58) / height));
    x = Math.max(18, (viewport.clientWidth - width * scale) / 2);
    y = Math.max(18, (viewport.clientHeight - height * scale) / 2);
    apply();
    sendZoom();
  }
  function zoom(factor, anchorX = viewport.clientWidth / 2, anchorY = viewport.clientHeight / 2) {
    if (!width) return;
    const next = Math.max(.000001, Math.min(3, scale * factor));
    x = anchorX - (anchorX - x) * next / scale;
    y = anchorY - (anchorY - y) * next / scale;
    scale = next;
    apply();
    sendZoom();
  }

  async function render(message, request) {
    if (request !== generation) return;
    version = message.version;
    closeMenu();
    if (fittedRevision !== message.diagramRevision) closeSearch();
    document.body.dataset.theme = message.theme;
    graph.replaceChildren();
    width = height = 0;
    syncScroll();
    feedback.textContent = (message.text || message.nodes) ? "図を描画しています…" : "";
    if (!message.text && !message.nodes) return;
    try {
      let fromMermaid = false;
      if (message.text) {
        try {
          window.flowMermaid.initialize({
            startOnLoad: false, securityLevel: "strict", theme: "base",
            maxTextSize: 400000, maxEdges: 1400, htmlLabels: false,
            themeVariables: { ...palettes[message.theme], fontFamily: "Segoe UI, sans-serif", fontSize: "15px" },
            flowchart: { htmlLabels: false, useMaxWidth: false, curve: "basis", nodeSpacing: 35, rankSpacing: 38 }
          });
          const { svg } = await window.flowMermaid.render("diagram" + request, message.text);
          if (request !== generation) return;
          graph.innerHTML = svg;
          fromMermaid = graph.querySelector(".node") !== null;
        } catch (error) {
          if (request !== generation) return;
          if (!message.nodes) throw error;
        }
      }
      if (!fromMermaid && message.nodes)
        graph.replaceChildren(window.renderLargeDiagram(message.nodes, message.edges));
      const svgNode = graph.firstElementChild;
      const labels = new Map(fromMermaid ? (message.nodes ?? []).map(node => [node.id, node.label]) : []);
      if (fromMermaid) {
        const textNodes = document.createTreeWalker(svgNode, NodeFilter.SHOW_TEXT);
        while (textNodes.nextNode()) {
          const label = textNodes.currentNode;
          if (label.parentElement.closest("text"))
            label.data = label.data.replace(/&#([0-9]{1,3});/g, (_, value) => String.fromCharCode(Number(value)));
        }
      }
      width = svgNode.viewBox.baseVal.width;
      height = svgNode.viewBox.baseVal.height;
      svgNode.style.width = width + "px";
      svgNode.style.height = height + "px";
      for (const node of svgNode.querySelectorAll(".node")) {
        const id = node.dataset.flowId ?? /^diagram\d+-flowchart-([a-z0-9_]+)-\d+$/.exec(node.id)?.[1];
        if (!id) continue;
        node.dataset.flowId = id;
        node.setAttribute("tabindex", "0");
        node.setAttribute("role", "button");
        if (labels.has(id)) {
          // Use the source label as text, including when an entity is split across tspans.
          const text = node.querySelector("text");
          const label = labels.get(id);
          const normalized = value => value.replace(/\s+/g, "");
          if (text && normalized(text.textContent) !== normalized(label)) {
            const firstLine = text.querySelector("tspan");
            if (firstLine) {
              firstLine.textContent = label;
              text.replaceChildren(firstLine);
            } else {
              text.textContent = label;
            }
          }
          node.setAttribute("aria-label", label);
        }
        const shape = node.querySelector(":scope > rect, :scope > polygon, :scope > path, :scope > circle");
        const text = node.querySelector("text");
        if (shape && text) {
          const shapeBounds = shape.getBoundingClientRect();
          const textBounds = text.getBoundingClientRect();
          // Leave extra room at the sloping sides of condition shapes.
          const availableWidth = shapeBounds.width * (node.classList.contains("flow_condition") ? .68 : .85);
          if (textBounds.width > availableWidth && availableWidth > 0) {
            text.setAttribute("textLength", String(text.getComputedTextLength() * availableWidth / textBounds.width));
            text.setAttribute("lengthAdjust", "spacingAndGlyphs");
          }
        }
      }
      feedback.textContent = "";
      if (message.preserveViewport && fittedRevision === message.diagramRevision) {
        apply();
        sendZoom();
      } else if (message.viewport) {
        ({ X: x, Y: y, Scale: scale } = message.viewport);
        apply();
        sendZoom();
      } else if (!fromMermaid && message.nodes) {
        scale = .8;
        x = viewport.clientWidth / 2 - width * scale / 2;
        y = 10;
        apply();
        const start = graph.querySelector(".flow_start");
        if (start) {
          const area = viewport.getBoundingClientRect();
          const bounds = start.getBoundingClientRect();
          x += area.left + area.width / 2 - (bounds.left + bounds.width / 2);
          apply();
        }
        sendZoom();
      } else {
        fit();
      }
      fittedRevision = message.diagramRevision;
      if (!search.hidden) updateSearch();
      send("rendered", null);
    } catch {
      if (request !== generation) return;
      graph.replaceChildren();
      feedback.textContent = "Mermaid プレビューを描画できません。ノード一覧または Mermaid 出力を利用できます。";
      send("error", null);
    }
  }

  function receive(message) {
    if (message.type === "render") {
      const request = ++generation;
      queue = queue.then(() => render(message, request));
    } else if (message.type === "fit") {
      fit();
    } else if (message.type === "zoom") {
      zoom(message.factor);
    } else if (message.type === "select") {
      selectNode(message.id, message.center);
    } else if (message.type === "search") {
      openSearch();
    } else if (message.type === "annotations") {
      const marks = new Set(message.marked);
      const gray = new Set(message.gray);
      for (const node of graph.querySelectorAll("[data-flow-id]")) {
        node.classList.toggle("marked", marks.has(node.dataset.flowId));
        node.classList.toggle("grayed", gray.has(node.dataset.flowId));
        for (const shape of node.querySelectorAll(":scope > rect, :scope > polygon, :scope > path, :scope > circle")) {
          if (!originalStyles.has(shape)) {
            const properties = ["fill", "stroke", "stroke-width"];
            originalStyles.set(shape, properties.map(name => [name, shape.style.getPropertyValue(name), shape.style.getPropertyPriority(name)]));
          }
          for (const [name, value, priority] of originalStyles.get(shape)) {
            if (value) shape.style.setProperty(name, value, priority);
            else shape.style.removeProperty(name);
          }
          if (marks.has(node.dataset.flowId)) {
            shape.style.setProperty("fill", "var(--mark-fill)", "important");
            shape.style.setProperty("stroke", "var(--mark-stroke)", "important");
            shape.style.setProperty("stroke-width", "4px", "important");
          }
        }
      }
    }
  }

  function selectNode(id, center) {
    graph.querySelector(".selected")?.classList.remove("selected");
    const node = graph.querySelector(`[data-flow-id="${CSS.escape(id)}"]`);
    if (!node) return;
    node.classList.add("selected");
    if (center) {
      const bounds = node.getBoundingClientRect();
      const area = viewport.getBoundingClientRect();
      // A fitted overview of a very large method may render text subpixel.
      const targetScale = Math.max(scale, .5);
      x = area.width / 2 - (bounds.left - area.left + bounds.width / 2 - x) / scale * targetScale;
      y = area.height / 2 - (bounds.top - area.top + bounds.height / 2 - y) / scale * targetScale;
      scale = targetScale;
      apply();
      sendZoom();
    }
  }

  function openSearch() {
    closeMenu();
    search.hidden = false;
    updateSearch();
    query.focus();
    query.select();
  }

  function closeSearch() {
    search.hidden = true;
    query.value = "";
    matchIndex = -1;
    matches = [];
    graph.querySelectorAll(".search-match").forEach(node => node.classList.remove("search-match"));
  }

  function updateSearch(move = false) {
    const selected = graph.querySelector(".selected")?.dataset.flowId;
    const term = query.value.trim().toLowerCase();
    matches = [];
    for (const node of graph.querySelectorAll("[data-flow-id]")) {
      const label = node.getAttribute("aria-label") ?? node.querySelector("text")?.textContent ?? "";
      const matched = term.length > 0 && label.toLowerCase().includes(term);
      node.classList.toggle("search-match", matched);
      if (matched) matches.push(node.dataset.flowId);
    }
    matchIndex = move ? -1 : matches.indexOf(selected);
    if (move && matches.length)
      stepSearch(1);
    else
      updateMatchCount();
  }

  function updateMatchCount() {
    matchCount.value = matchIndex >= 0 ? `${matchIndex + 1} / ${matches.length}` : `${matches.length} 件`;
    search.querySelectorAll("[data-step], [type=submit]").forEach(button => button.disabled = !matches.length);
  }

  function stepSearch(delta) {
    if (!matches.length) return;
    const previous = matchIndex < 0 && delta < 0 ? 0 : matchIndex;
    matchIndex = (previous + delta + matches.length) % matches.length;
    const id = matches[matchIndex];
    selectNode(id, true);
    send("node", id);
    updateMatchCount();
  }

  query.addEventListener("input", () => updateSearch(true));
  search.addEventListener("submit", event => {
    event.preventDefault();
    stepSearch(1);
  });
  search.querySelector("[data-step]").addEventListener("click", () => stepSearch(-1));
  search.querySelector("[data-close]").addEventListener("click", () => {
    closeSearch();
    viewport.focus({ preventScroll: true });
  });
  window.addEventListener("keydown", event => {
    if (event.ctrlKey && !event.altKey && !event.metaKey && event.key.toLowerCase() === "f") {
      event.preventDefault();
      openSearch();
    } else if (event.altKey && !event.ctrlKey && event.key === "ArrowLeft") {
      event.preventDefault();
      send("back", null);
    } else if (event.key === "Escape" && !search.hidden && menu.hidden) {
      event.preventDefault();
      closeSearch();
      viewport.focus({ preventScroll: true });
    } else if (event.key === "Enter" && event.shiftKey && event.target === query) {
      event.preventDefault();
      stepSearch(-1);
    }
  });

  function closeMenu() {
    menu.hidden = true;
    menuNode = undefined;
  }

  graph.addEventListener("click", event => {
    const node = event.target.closest("[data-flow-id]");
    if (!node || suppressClick) return;
    selectNode(node.dataset.flowId);
    send("node", node.dataset.flowId);
  });
  graph.addEventListener("keydown", event => {
    const node = event.target.closest("[data-flow-id]");
    if (!node || (event.key !== "Enter" && event.key !== " ")) return;
    event.preventDefault();
    selectNode(node.dataset.flowId);
    send("node", node.dataset.flowId);
  });
  viewport.addEventListener("contextmenu", event => {
    event.preventDefault();
    const node = event.target.closest("[data-flow-id]");
    if (!node) { closeMenu(); return; }
    menuNode = node.dataset.flowId;
    menu.hidden = false;
    menu.style.left = Math.max(0, Math.min(event.clientX, viewport.clientWidth - menu.offsetWidth)) + "px";
    menu.style.top = Math.max(0, Math.min(event.clientY, viewport.clientHeight - menu.offsetHeight)) + "px";
    menu.querySelector("button").focus();
  });
  menu.addEventListener("click", event => {
    const action = event.target.closest("[data-action]")?.dataset.action;
    if (!action || !menuNode) return;
    send("annotate", { id: menuNode, action });
    closeMenu();
  });
  viewport.addEventListener("keydown", event => {
    if (event.key === "Escape" && !menu.hidden) {
      event.preventDefault();
      event.stopPropagation();
      closeMenu();
    }
  });

  viewport.addEventListener("wheel", event => {
    if (scroller.contains(event.target) || search.contains(event.target) || menu.contains(event.target)) return;
    event.preventDefault();
    zoom(event.deltaY < 0 ? 1.13 : 1 / 1.13, event.clientX, event.clientY);
  }, { passive: false });
  viewport.addEventListener("pointerdown", event => {
    if (event.button !== 0 || menu.contains(event.target) || scroller.contains(event.target) || search.contains(event.target)) return;
    closeMenu();
    if (!event.target.closest("[data-flow-id]")) viewport.focus({ preventScroll: true });
    drag = { id: event.pointerId, startX: event.clientX, startY: event.clientY, x, y };
    suppressClick = false;
  });
  viewport.addEventListener("pointermove", event => {
    if (!drag || drag.id !== event.pointerId) return;
    const dx = event.clientX - drag.startX, dy = event.clientY - drag.startY;
    if (!suppressClick && Math.hypot(dx, dy) < 5) return;
    if (!suppressClick) {
      viewport.setPointerCapture(event.pointerId);
      graph.classList.add("dragging");
    }
    suppressClick = true;
    x = drag.x + dx;
    y = drag.y + dy;
    apply();
  });
  for (const type of ["pointerup", "pointercancel", "lostpointercapture"]) {
    viewport.addEventListener(type, () => { drag = undefined; graph.classList.remove("dragging"); });
  }
  host?.addEventListener("message", event => receive(event.data));
  window.flowPreview = receive;
  send(typeof window.flowMermaid?.render === "function" ? "ready" : "startup-error", null);
})();
