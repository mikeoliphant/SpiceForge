// Bundles src/main.ts into dist/app.js and copies the static files beside it. Type checking is `tsc --noEmit`
// (npm run build does both); esbuild only strips the types. `node build.mjs --watch` rebuilds on every change;
// `node build.mjs --test` bundles the conformance test instead (npm test).
import * as esbuild from "esbuild";
import { copyFile, mkdir } from "node:fs/promises";

const watch = process.argv.includes("--watch");
const statics = ["index.html", "themes.css", "style.css"];

async function copyStatics() {
  await mkdir("dist", { recursive: true });
  await Promise.all(statics.map((f) => copyFile(`static/${f}`, `dist/${f}`)));
}

const options = {
  entryPoints: ["src/main.ts"],
  bundle: true,
  format: "esm",
  target: "es2022",
  sourcemap: true,
  outfile: "dist/app.js",
  logLevel: "info",
  plugins: [{ name: "statics", setup: (build) => build.onEnd(copyStatics) }],
};

if (process.argv.includes("--test")) {
  // The conformance test runs under Node, against a running server.
  await esbuild.build({
    entryPoints: ["test/conformance.ts"],
    bundle: true,
    format: "esm",
    platform: "node",
    target: "node22",
    outfile: "dist-test/conformance.mjs",
    logLevel: "warning",
  });
} else if (watch) {
  const context = await esbuild.context(options);
  await context.watch();
} else {
  await esbuild.build(options);
}
