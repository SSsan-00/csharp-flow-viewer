import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { createServer } from "node:http";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright-core";

const [baseline] = process.argv.slice(2);
assert.ok(baseline, "Usage: node benchmark.mjs <baseline checkout>");
const current = fileURLToPath(new URL("../..", import.meta.url));
const files = new Map([
  ["index.html", "text/html"], ["preview.css", "text/css"], ["preview.js", "text/javascript"],
  ["large-diagram.js", "text/javascript"], ["mermaid.bundle.js", "text/javascript"]
]);
const assets = new Map();
for (const [tag, path] of [["before", baseline], ["after", current]]) {
  for (const [name, type] of files) {
    assets.set(`/${tag}/${name}`, [await readFile(resolve(path, "src/Flow.Desktop/Preview", name)), type]);
  }
}

const server = createServer((request, response) => {
  const asset = assets.get(request.url);
  if (!asset) return response.writeHead(404).end();
  response.writeHead(200, { "Content-Type": asset[1] + "; charset=utf-8" }).end(asset[0]);
});
await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
let browser;
try {
  browser = await chromium.launch(process.env.CFV_CHROMIUM
    ? { executablePath: process.env.CFV_CHROMIUM }
    : { channel: "msedge" });
  console.log("Browser:", browser.version());
  const origin = "http://127.0.0.1:" + server.address().port;
  const cases = [400, 2400];
  const runs = { before: new Map(), after: new Map() };
  for (const tag of ["before", "after", "after", "before"]) {
    const page = await browser.newPage({ viewport: { width: 1150, height: 800 } });
    await page.route("**/*", route => route.request().url().startsWith(origin + "/") ? route.continue() : route.abort());
    await page.addInitScript(() => {
      window.chrome.webview = {
        postMessage(data) { if (data.type === "rendered") window.finishRender(performance.now()); },
        addEventListener() {}
      };
    });
    await page.goto(origin + "/" + tag + "/index.html");
    for (const count of cases) {
      const result = await page.evaluate(async count => {
        const nodes = Array.from({ length: count }, (_, index) => ({
          id: "n" + index, label: index % 3 ? "check(" + index + ") " + "長い条件式".repeat(12) : "Short " + index,
          kind: index === 0 ? "start" : (index % 2 ? "call" : "condition"), reachable: true
        }));
        const edges = nodes.slice(1).map((node, i) => ({ from: nodes[i].id, to: node.id, reachable: true }));
        edges.push({ from: nodes.at(-1).id, to: "n4", label: "False", reachable: true });
        const times = [];
        for (let version = 1; version <= 4; version++) {
          const start = performance.now();
          const end = await new Promise(resolve => {
            window.finishRender = resolve;
            window.flowPreview({ type: "render", theme: "light", version, diagramRevision: version, nodes, edges });
          });
          if (version > 1) times.push(end - start);
        }
        const svg = document.querySelector("#graph svg");
        const shape = [...svg.querySelectorAll(".node")].map(node => {
          const label = node.querySelector("text");
          return [node.dataset.flowId, node.getAttribute("transform"), label.textContent, label.getAttribute("textLength")];
        });
        return { times, shape, edges: [...svg.querySelectorAll(".large-link")].map(edge => edge.getAttribute("d")) };
      }, count);
      assert.equal(result.shape.length, count);
      assert.equal(result.edges.length, count);
      assert.ok(result.shape.some(([, , , length]) => length !== null));
      const initial = runs[tag].get(count);
      if (!initial) runs[tag].set(count, result);
      else {
        assert.deepEqual(initial.shape, result.shape);
        assert.deepEqual(initial.edges, result.edges);
        initial.times.push(...result.times);
      }
    }
    await page.close();
  }
  const median = times => {
    const ordered = [...times].sort((a, b) => a - b);
    return (ordered[(ordered.length - 1) >> 1] + ordered[ordered.length >> 1]) / 2;
  };
  for (const count of cases) {
    const before = runs.before.get(count), after = runs.after.get(count);
    assert.deepEqual(before.shape, after.shape);
    assert.deepEqual(before.edges, after.edges);
    console.log(JSON.stringify({ count, beforeMs: median(before.times), afterMs: median(after.times),
      runsPerVersion: before.times.length, identicalSvgGeometry: true }));
  }
} finally {
  await browser?.close();
  server.close();
}
