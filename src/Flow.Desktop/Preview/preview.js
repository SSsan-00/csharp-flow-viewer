(() => {
  const viewport = document.querySelector("#viewport");
  const graph = document.querySelector("#graph");
  const feedback = document.querySelector("#feedback");
  const host = window.chrome?.webview;
  const palettes = {
    dark: { background: "#101521", lineColor: "#b7bfd7", edgeLabelBackground: "#222a3b", textColor: "#e7e9f7", nodeTextColor: "#e7e9f7" },
    light: { background: "#f7f8fc", lineColor: "#43516b", edgeLabelBackground: "#ecf0fa", textColor: "#232b43", nodeTextColor: "#232b43" }
  };

  let width = 0, height = 0, x = 0, y = 0, scale = 1;
  let generation = 0, version = 0, drag, suppressClick;
  let queue = Promise.resolve();
  const send = (type, value) => host?.postMessage({ type, value, version });

  function apply() {
    graph.style.transform = `translate(${x}px, ${y}px) scale(${scale})`;
  }
  function fit() {
    if (!width || !height) return;
    scale = Math.min(1, Math.max(.12, (viewport.clientWidth - 52) / width),
      Math.max(.12, (viewport.clientHeight - 58) / height));
    x = Math.max(18, (viewport.clientWidth - width * scale) / 2);
    y = Math.max(18, (viewport.clientHeight - height * scale) / 2);
    apply();
    send("zoom", Math.round(scale * 100));
  }
  function zoom(factor, anchorX = viewport.clientWidth / 2, anchorY = viewport.clientHeight / 2) {
    if (!width) return;
    const next = Math.max(.08, Math.min(3, scale * factor));
    x = anchorX - (anchorX - x) * next / scale;
    y = anchorY - (anchorY - y) * next / scale;
    scale = next;
    apply();
    send("zoom", Math.round(scale * 100));
  }

  async function render(message, request) {
    if (request !== generation) return;
    version = message.version;
    document.body.dataset.theme = message.theme;
    graph.replaceChildren();
    width = height = 0;
    feedback.textContent = message.text ? "図を描画しています…" : "";
    if (!message.text) return;
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
      const svgNode = graph.firstElementChild;
      // Strict SVG labels can leave Mermaid's numeric entities in text nodes.
      // Resolve them as text after sanitization without interpreting any markup.
      const labels = document.createTreeWalker(svgNode, NodeFilter.SHOW_TEXT);
      while (labels.nextNode()) {
        const label = labels.currentNode;
        if (label.parentElement.closest("text"))
          label.data = label.data.replace(/&#([0-9]{1,3});/g, (_, value) => String.fromCharCode(Number(value)));
      }
      width = svgNode.viewBox.baseVal.width;
      height = svgNode.viewBox.baseVal.height;
      svgNode.style.width = width + "px";
      svgNode.style.height = height + "px";
      for (const node of svgNode.querySelectorAll(".node[id]")) {
        const id = /^diagram\d+-flowchart-([a-z0-9_]+)-\d+$/.exec(node.id)?.[1];
        if (!id) continue;
        node.dataset.flowId = id;
        node.setAttribute("tabindex", "0");
        node.setAttribute("role", "button");
        const select = () => {
          graph.querySelector(".selected")?.classList.remove("selected");
          node.classList.add("selected");
          send("node", id);
        };
        node.addEventListener("click", () => { if (!suppressClick) select(); });
        node.addEventListener("keydown", event => {
          if (event.key === "Enter" || event.key === " ") {
            event.preventDefault();
            select();
          }
        });
      }
      feedback.textContent = "";
      fit();
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
      graph.querySelector(".selected")?.classList.remove("selected");
      graph.querySelector(`[data-flow-id="${CSS.escape(message.id)}"]`)?.classList.add("selected");
    }
  }

  viewport.addEventListener("wheel", event => {
    event.preventDefault();
    zoom(event.deltaY < 0 ? 1.13 : 1 / 1.13, event.clientX, event.clientY);
  }, { passive: false });
  viewport.addEventListener("pointerdown", event => {
    if (event.button !== 0) return;
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
  send("ready", null);
})();
