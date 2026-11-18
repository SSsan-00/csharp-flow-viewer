import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { createServer } from "node:http";
import { chromium } from "playwright-core";

const fixture = process.env.CFV_MERMAID_FIXTURE;
assert.ok(fixture, "Set CFV_MERMAID_FIXTURE and run the .NET tests before running this test.");
const source = await readFile(fixture, "utf8");
const assets = new Map();
for (const [file, type] of [["index.html", "text/html"], ["preview.css", "text/css"],
  ["preview.js", "text/javascript"], ["mermaid.bundle.js", "text/javascript"]]) {
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
      addEventListener() {}
    };
  });
  const origin = "http://127.0.0.1:" + server.address().port;
  await page.route("**/*", route => route.request().url().startsWith(origin + "/") ? route.continue() : route.abort());
  await page.goto(origin + "/index.html");
  assert.ok(await page.evaluate(text => window.flowMermaid.parse(text), source));
  await page.evaluate(text => window.flowPreview({ type: "render", theme: "dark", version: 1, text }), source);
  await page.waitForFunction(() => window.previewMessages.some(message => message.type === "rendered"));
  const condition = page.locator("#graph .node.flow_condition");
  assert.match(await condition.textContent(), /保存|値/);
  assert.match(await condition.textContent(), /<script>/);
  assert.equal(await page.locator("#graph script").count(), 0);
  assert.equal(await page.locator("#graph .edgeLabel").filter({ hasText: "True" }).count(), 1);
  await condition.click();
  const picked = await page.evaluate(() => window.previewMessages.findLast(message => message.type === "node"));
  assert.equal(picked.value, await condition.getAttribute("data-flow-id"));
  const oldTransform = await page.locator("#graph").evaluate(node => node.style.transform);
  await page.mouse.move(20, 20);
  await page.mouse.down();
  await page.mouse.move(110, 105);
  await page.mouse.up();
  assert.notEqual(await page.locator("#graph").evaluate(node => node.style.transform), oldTransform);
  await page.evaluate(() => window.flowPreview({ type: "zoom", factor: 1.2 }));
  assert.ok(await page.evaluate(() => window.previewMessages.filter(message => message.type === "zoom").length >= 2));
  await page.setViewportSize({ width: 740, height: 510 });
  await page.evaluate(text => window.flowPreview({ type: "render", theme: "light", version: 2, text }), source);
  await page.waitForFunction(() => window.previewMessages.some(message => message.type === "rendered" && message.version === 2));
  assert.equal(await page.locator("body").getAttribute("data-theme"), "light");
  await page.evaluate(() => window.flowPreview({ type: "fit" }));
  console.log("Mermaid parser and Chromium preview: escaped labels, edge labels, selection, pan, zoom, resize, light/dark passed.");
} finally {
  await browser?.close();
  server.close();
}
