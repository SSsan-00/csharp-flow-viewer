import { build } from "esbuild";
import { readFile, writeFile, readdir } from "node:fs/promises";
import { dirname, resolve } from "node:path";

const output = "../../src/Flow.Desktop/Preview/mermaid.bundle.js";
const bundle = await build({
  stdin: { contents: 'import mermaid from "mermaid"; window.flowMermaid = mermaid;', resolveDir: process.cwd() },
  bundle: true, minify: true, platform: "browser", target: "es2022", outfile: output,
  legalComments: "eof", metafile: true
});

const packages = new Set();
for (const file of Object.keys(bundle.metafile.inputs)) {
  let directory = dirname(resolve(file));
  while (directory.includes("node_modules")) {
    // Some packages include package.json files solely to select the module type.
    const manifest = await readFile(resolve(directory, "package.json"), "utf8").catch(error => {
      if (error.code !== "ENOENT") throw error;
    });
    if (manifest && JSON.parse(manifest).name) {
      packages.add(directory);
      break;
    }
    directory = dirname(directory);
  }
}
const notices = [];
for (const directory of [...packages].sort()) {
  const { name, version, license } = JSON.parse(await readFile(resolve(directory, "package.json"), "utf8"));
  const files = (await readdir(directory)).filter(file => /^(licen[sc]e|copying|notice)(\b|_)/i.test(file));
  if (!files.length) throw new Error(`Missing license text for ${name}@${version}`);
  notices.push(`${name}@${version} (${JSON.stringify(license)})\n`);
  for (const file of files.sort())
    notices.push(`${file}\n${await readFile(resolve(directory, file), "utf8")}\n`);
}
await writeFile("THIRD-PARTY-LICENSES.txt", notices.join("\n"));
