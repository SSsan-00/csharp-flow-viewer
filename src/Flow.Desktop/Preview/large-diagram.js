// Lay out the CFG in linear passes. Back edges are routed separately so loops
// do not prevent the forward graph from being arranged in ranks.
window.renderLargeDiagram = (nodes, edges) => {
  const ns = "http://www.w3.org/2000/svg";
  const element = (tag, attrs = {}, text) => {
    const node = document.createElementNS(ns, tag);
    for (const [name, value] of Object.entries(attrs)) node.setAttribute(name, value);
    if (text !== undefined) node.textContent = text;
    return node;
  };

  const byId = new Map(nodes.map(node => [node.id, {
    ...node, state: 0, rank: 0, incoming: 0, outgoing: []
  }]));
  const links = edges.map(edge => ({
    ...edge, source: byId.get(edge.from), target: byId.get(edge.to), back: false
  }));
  for (const edge of links) edge.source.outgoing.push(edge);

  for (const root of byId.values()) {
    if (root.state) continue;
    root.state = 1;
    const stack = [{ node: root, next: 0 }];
    while (stack.length) {
      const frame = stack.at(-1);
      if (frame.next === frame.node.outgoing.length) {
        frame.node.state = 2;
        stack.pop();
        continue;
      }
      const edge = frame.node.outgoing[frame.next++];
      if (edge.target.state === 1) {
        edge.back = true;
        continue;
      }
      edge.target.incoming++;
      if (edge.target.state === 0) {
        edge.target.state = 1;
        stack.push({ node: edge.target, next: 0 });
      }
    }
  }

  const ranks = [];
  const queue = [...byId.values()].filter(node => !node.incoming);
  for (let i = 0; i < queue.length; i++) {
    const node = queue[i];
    (ranks[node.rank] ??= []).push(node);
    for (const edge of node.outgoing) {
      if (edge.back) continue;
      edge.target.rank = Math.max(edge.target.rank, node.rank + 1);
      if (--edge.target.incoming === 0) queue.push(edge.target);
    }
  }

  const spacing = 380, step = 104, margin = 88;
  const columns = ranks.reduce((maximum, rank) => Math.max(maximum, rank.length), 1);
  const width = columns * spacing + margin * 2;
  const height = ranks.length * step + 40;
  for (const rank of ranks) {
    rank.forEach((node, index) => {
      node.x = width / 2 + (index - (rank.length - 1) / 2) * spacing;
      node.y = 48 + node.rank * step;
    });
  }

  const svg = element("svg", { viewBox: `0 0 ${width} ${height}`, class: "large-diagram" });
  const defs = element("defs");
  const arrow = element("marker", { id: "large-arrow", viewBox: "0 0 10 10",
    refX: 9, refY: 5, markerWidth: 7, markerHeight: 7, orient: "auto" });
  arrow.append(element("path", { d: "M 0 0 L 10 5 L 0 10 z", class: "large-arrow" }));
  defs.append(arrow);
  svg.append(defs);
  links.forEach(({ source, target, back, label, reachable }, index) => {
    const sideRoute = back || target.rank - source.rank > 1;
    const left = !back;
    const gutter = left ? 20 + index % 7 * 8 : width - 20 - index % 7 * 8;
    const outlet = left ? -156 : 156;
    const middle = (source.y + target.y) / 2;
    const d = sideRoute
      ? `M ${source.x + outlet} ${source.y} H ${gutter} V ${target.y - 32} H ${target.x} V ${target.y - 25}`
      : `M ${source.x} ${source.y + 24} C ${source.x} ${middle}, ${target.x} ${middle}, ${target.x} ${target.y - 25}`;
    svg.append(element("path", { d, class: "large-link" + (reachable ? "" : " unreachable-link"),
      "marker-end": "url(#large-arrow)" }));
    if (label) svg.append(element("text", {
      x: sideRoute ? source.x + outlet + (left ? -28 : 28) : (source.x + target.x) / 2 + 12,
      y: sideRoute ? source.y - 7 : middle - 8,
      class: "large-edge-label"
    }, label));
  });

  for (const node of byId.values()) {
    const group = element("g", { class: "node flow_" + (node.reachable ? node.kind : "unreachable"),
      "data-flow-id": node.id, transform: `translate(${node.x},${node.y})` });
    const terminal = node.kind === "start" || node.kind === "end";
    group.append(node.kind === "condition"
      ? element("polygon", { points: "-156,0 -126,-24 126,-24 156,0 126,24 -126,24" })
      : element("rect", { x: -156, y: -24, width: 312, height: 48, rx: terminal ? 24 : 6 }));
    const label = element("text", { "text-anchor": "middle", "dominant-baseline": "central" }, node.label);
    group.append(label, element("title", {}, node.label));
    svg.append(group);
  }
  return svg;
};
