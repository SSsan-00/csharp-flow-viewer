import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { createServer } from "node:http";
import { chromium } from "playwright-core";

const fixture = process.env.CFV_MERMAID_FIXTURE;
assert.ok(fixture, "Set CFV_MERMAID_FIXTURE and run the .NET tests before running this test.");
const source = await readFile(fixture, "utf8");
const payload = JSON.parse(await readFile(fixture + ".json", "utf8"));
const assets = new Map();
for (const [file, type] of [["index.html", "text/html"], ["preview.css", "text/css"],
  ["preview.js", "text/javascript"], ["large-diagram.js", "text/javascript"], ["mermaid.bundle.js", "text/javascript"]]) {
  assets.set("/" + file, [await readFile(new URL("../../src/Flow.Desktop/Preview/" + file, import.meta.url)), type]);
}
const server = createServer((request, response) => {
  const asset = assets.get(request.url);
  if (!asset) return response.writeHead(404).end();
  response.writeHead(200, { "Content-Type": asset[1] + "; charset=utf-8" }).end(asset[0]);
});
await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
let browser;
try {
  browser = await chromium.launch({ executablePath: process.env.CFV_CHROMIUM, args: ["--no-sandbox"] });
  const page = await browser.newPage({ viewport: { width: 1150, height: 800 } });
  await page.addInitScript(() => {
    window.previewMessages = [];
    window.chrome.webview = {
      postMessage(data) { window.previewMessages.push(data); },
      addEventListener(name, listener) {
        if (name !== "message") throw new Error("Unexpected WebView event: " + name);
        window.receiveFromHost = data => listener({ data });
      }
    };
  });
  const origin = "http://127.0.0.1:" + server.address().port;
  await page.route("**/*", route => route.request().url().startsWith(origin + "/") ? route.continue() : route.abort());
  await page.goto(origin + "/index.html");
  assert.ok(await page.evaluate(text => window.flowMermaid.parse(text), source));
  await page.evaluate(({ text, nodes, edges }) => window.receiveFromHost({
    type: "render", theme: "dark", version: 1, diagramRevision: 1, text, nodes, edges
  }), { text: source, ...payload });
  await page.waitForFunction(() => window.previewMessages.some(message => message.type === "rendered"));
  const condition = page.locator("#graph .node.flow_condition");
  assert.match(await condition.textContent(), /保存|値/);
  assert.match(await condition.textContent(), /<script>/);
  assert.equal(await page.locator("#graph script").count(), 0);
  assert.equal(await page.locator("#graph .edgeLabel").filter({ hasText: "True" }).count(), 1);
  await condition.click();
  const picked = await page.evaluate(() => window.previewMessages.findLast(message => message.type === "node"));
  assert.equal(picked.value, await condition.getAttribute("data-flow-id"));
  assert.equal(await condition.locator(".selection-ring").count(), 1);
  assert.equal(await condition.locator(".selection-ring").evaluate(shape => getComputedStyle(shape).stroke), "rgb(85, 234, 255)");
  const oldTransform = await page.locator("#graph").evaluate(node => node.style.transform);
  await page.mouse.move(20, 20);
  await page.mouse.down();
  await page.mouse.move(110, 105);
  await page.mouse.up();
  assert.notEqual(await page.locator("#graph").evaluate(node => node.style.transform), oldTransform);
  await page.evaluate(() => window.receiveFromHost({ type: "zoom", factor: 1.2 }));
  assert.ok(await page.evaluate(() => window.previewMessages.filter(message => message.type === "zoom").length >= 2));
  await page.setViewportSize({ width: 740, height: 510 });
  const graph = page.locator("#graph");
  const zoomedTransform = await graph.evaluate(node => node.style.transform);
  const rawLabel = "長い条件式".repeat(24) + " Check(値) <script>？";
  await page.evaluate(({ text, id, label, nodes, edges }) => window.receiveFromHost({
    type: "render", theme: "light", version: 2, diagramRevision: 1, preserveViewport: true, text,
    nodes: nodes.map(node => node.id === id ? { ...node, label } : node), edges
  }), { text: source, id: picked.value, label: rawLabel, ...payload });
  await page.waitForFunction(() => window.previewMessages.some(message => message.type === "rendered" && message.version === 2));
  assert.equal(await page.locator("body").getAttribute("data-theme"), "light");
  assert.equal(await graph.evaluate(node => node.style.transform), zoomedTransform);
  assert.equal(await condition.locator("text").textContent(), rawLabel);
  await page.evaluate(id => window.receiveFromHost({ type: "select", id, center: false }), picked.value);
  assert.equal(await condition.locator(".selection-ring").evaluate(shape => getComputedStyle(shape).stroke), "rgb(0, 82, 187)");
  assert.ok(await condition.evaluate(node => {
    const shape = node.querySelector(":scope > rect, :scope > polygon, :scope > path, :scope > circle").getBoundingClientRect();
    const label = node.querySelector("text").getBoundingClientRect();
    return label.left >= shape.left && label.right <= shape.right;
  }), "Mermaid label should fit within its node");
  assert.equal(await page.locator("#graph script").count(), 0);
  await page.evaluate(() => window.receiveFromHost({ type: "fit" }));
  await condition.click({ button: "right" });
  await page.getByRole("menuitem", { name: "マーク" }).click();
  const annotation = await page.evaluate(() => window.previewMessages.findLast(message => message.type === "annotate"));
  assert.deepEqual(annotation.value, { id: picked.value, action: "mark" });
  await page.evaluate(id => window.receiveFromHost({ type: "annotations", marked: [id], gray: [] }), picked.value);
  assert.equal(await graph.locator(".marked").count(), 1);
  assert.equal(await condition.locator(".selection-ring").evaluate(shape => getComputedStyle(shape).stroke), "rgb(0, 82, 187)");
  const markedShape = condition.locator(":scope > rect, :scope > polygon, :scope > path, :scope > circle").first();
  assert.equal(await markedShape.evaluate(shape => getComputedStyle(shape).fill), "rgb(255, 229, 161)");
  assert.equal(await markedShape.evaluate(shape => getComputedStyle(shape).stroke), "rgb(123, 82, 0)");
  await page.evaluate(id => window.receiveFromHost({ type: "annotations", marked: [], gray: [id] }), picked.value);
  assert.notEqual(await markedShape.evaluate(shape => getComputedStyle(shape).fill), "rgb(255, 229, 161)");
  assert.equal(await graph.locator(".grayed").count(), 1);

  await page.evaluate(() => {
    const nodes = Array.from({ length: 8002 }, (_, i) => ({
      id: "n" + i, label: i === 0 ? "幅の広い文字列".repeat(20) : "Call(" + i + ")", kind: i === 0 ? "start" : "call", reachable: true
    }));
    const edges = nodes.slice(1).map((node, i) => ({ from: nodes[i].id, to: node.id, reachable: true }));
    edges.push({ from: "n8001", to: "n4", label: "False", reachable: true });
    window.receiveFromHost({ type: "render", theme: "light", version: 3, diagramRevision: 2, text: null, nodes, edges });
  });
  await page.waitForFunction(() => window.previewMessages.some(message => message.type === "rendered" && message.version === 3));
  assert.equal(await graph.locator(".large-diagram .node").count(), 8002);
  assert.equal(await graph.locator(".large-link").count(), 8002);
  assert.ok(await graph.locator('[data-flow-id="n0"]').evaluate(node => {
    const shape = node.querySelector("rect").getBoundingClientRect();
    const label = node.querySelector("text").getBoundingClientRect();
    return label.left >= shape.left && label.right <= shape.right;
  }), "Large diagram label should fit within its node");
  const last = graph.locator('[data-flow-id="n8001"]');
  await page.evaluate(() => window.receiveFromHost({ type: "select", id: "n8001", center: true }));
  assert.equal(await last.getAttribute("class"), "node flow_call selected");
  assert.ok(await last.evaluate(node => {
    const box = node.getBoundingClientRect();
    return box.top >= 0 && box.bottom <= window.innerHeight;
  }));
  await page.keyboard.press("Control+f");
  await page.getByRole("searchbox", { name: "ノードを検索" }).fill("Call(800");
  assert.equal(await page.locator("#preview-search output").textContent(), "1 / 3");
  await page.getByRole("button", { name: "次の一致" }).click();
  assert.equal(await page.locator("#preview-search output").textContent(), "2 / 3");
  await page.getByRole("button", { name: "次の一致" }).click();
  assert.equal(await page.locator("#preview-search output").textContent(), "3 / 3");
  const found = await page.evaluate(() => window.previewMessages.findLast(message => message.type === "node"));
  assert.equal(found.value, "n8001");
  await page.keyboard.press("Escape");

  const scroll = page.locator("#vertical-scroll");
  assert.ok(await scroll.isVisible());
  await scroll.evaluate(element => element.scrollTop = 0);
  await page.waitForFunction(() => document.querySelector("#graph").style.transform.includes(", 18px)"));
  await scroll.evaluate(element => element.scrollTop = 325);
  await page.waitForFunction(() => document.querySelector("#graph").style.transform.includes(", -307px)"));

  await page.evaluate(() => window.receiveFromHost({ type: "annotations", marked: ["n0"], gray: ["n0"] }));
  const stroke = await graph.locator('[data-flow-id="n0"] rect').evaluate(shape => getComputedStyle(shape).stroke);
  assert.equal(stroke, "rgb(123, 82, 0)");
  await page.evaluate(() => {
    const original = document.querySelector('[data-flow-id="n2"] > rect');
    original.style.setProperty("fill", "rgb(20, 40, 60)", "important");
    window.receiveFromHost({ type: "annotations", marked: ["n2", "n2", "missing"], gray: ["n5"] });
    window.receiveFromHost({ type: "annotations", marked: ["n2"], gray: ["n5"] });
  });
  assert.equal(await graph.locator(".marked").count(), 1);
  assert.equal(await graph.locator(".marked").getAttribute("data-flow-id"), "n2");
  assert.equal(await graph.locator(".grayed").getAttribute("data-flow-id"), "n5");
  assert.equal(await graph.locator('[data-flow-id="n2"] rect').evaluate(shape => getComputedStyle(shape).stroke), "rgb(123, 82, 0)");
  await page.evaluate(() => window.receiveFromHost({ type: "annotations", marked: [], gray: [] }));
  assert.equal(await graph.locator(".marked,.grayed").count(), 0);
  assert.deepEqual(await graph.locator('[data-flow-id="n2"] rect').evaluate(shape =>
    [shape.style.getPropertyValue("fill"), shape.style.getPropertyPriority("fill")]), ["rgb(20, 40, 60)", "important"]);
  await page.evaluate(() => window.receiveFromHost({ type: "annotations", marked: ["n0"], gray: ["n0"] }));
  const savedViewport = await page.evaluate(() => window.previewMessages.findLast(message => message.type === "viewport").value);
  const savedTransform = await graph.evaluate(node => node.style.transform);
  await page.evaluate(data => window.receiveFromHost({ type: "render", theme: "dark", version: 4, diagramRevision: 3, ...data }),
    { text: source, ...payload });
  await page.waitForFunction(() => window.previewMessages.some(message => message.type === "rendered" && message.version === 4));
  assert.equal(await graph.locator(".marked").count(), 0);
  await condition.click({ button: "right" });
  await page.getByRole("menuitem", { name: "マーク" }).click();
  const newMark = await page.evaluate(() => window.previewMessages.findLast(message => message.type === "annotate"));
  assert.equal(newMark.version, 4);
  await page.evaluate(data => window.receiveFromHost({
    type: "render", theme: "dark", version: 5, diagramRevision: 4, ...data
  }), { text: source, viewport: savedViewport, ...payload });
  await page.waitForFunction(() => window.previewMessages.some(message => message.type === "rendered" && message.version === 5));
  assert.equal(await graph.evaluate(node => node.style.transform), savedTransform);
  const spaces = 'value == "A  B"';
  const whitespaceSource = 'flowchart TD\n  n0{"value #61;#61; #34;A #160;B#34;"}:::flow_condition';
  for (const version of [6, 7]) {
    await page.evaluate(({ version, text, label }) => window.receiveFromHost({
      type: "render", theme: "dark", version, diagramRevision: version, text, nodes: [{ id: "n0", kind: "condition", label, reachable: true }], edges: []
    }), { version, text: version === 6 ? whitespaceSource : null, label: spaces });
    await page.waitForFunction(version => window.previewMessages.some(m => m.type === "rendered" && m.version === version), version);
    const label = graph.locator('[data-flow-id="n0"] text');
    assert.equal((await label.textContent()).replaceAll("\u00a0", " "), spaces);
    assert.equal(await label.evaluate(text => getComputedStyle(text).whiteSpace), "pre");
  }
  await page.evaluate(text => window.receiveFromHost({ type: "render", theme: "dark", version: 8, text }), source);
  await page.waitForFunction(() => window.previewMessages.some(m => m.type === "error" && m.version === 8));
  assert.equal(await graph.locator("svg").count(), 0);
  await page.evaluate(data => window.receiveFromHost({ type: "render", theme: "dark", version: 9, diagramRevision: 9,
    text: "deliberately invalid Mermaid", ...data }), payload);
  await page.waitForFunction(() => window.previewMessages.some(m => m.type === "rendered" && m.version === 9));
  assert.equal(await graph.locator(".large-diagram .node").count(), payload.nodes.length);
  console.log("Preview passed: Mermaid labels, zoom preservation, annotations, and 8,002-node SVG with a back edge.");
} finally {
  await browser?.close();
  server.close();
}
