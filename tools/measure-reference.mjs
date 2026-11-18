import { createServer } from "node:http";
import { readFileSync, mkdirSync, writeFileSync } from "node:fs";
import { createRequire } from "node:module";
import { join, resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { once } from "node:events";

const [reference, modules, chrome, resolution = "1280x720", quality = "0", mode = "capture"] = process.argv.slice(2);
if (!reference || !modules || !chrome || !/^[012]$/.test(quality) || !["capture", "benchmark"].includes(mode))
  throw new Error("Usage: node tools/measure-reference.mjs <upstream checkout> <node_modules> <chrome.exe> [1280x720] [0|1|2] [capture|benchmark]");

const [width, height] = resolution.split("x").map(Number);
if (!width || !height) throw new Error("Expected a width and height in pixels.");
const { chromium } = await import(pathToFileURL(resolve(modules, "playwright-core/index.mjs")));
const sources = ["quality", "math", "shaders", "world", "main", "audio"].map((part) =>
  readFileSync(join(reference, "source", "src", part + ".js"), "utf8")
);
const autoLoop = "requestAnimationFrame((t) => { F[LAST] = t; requestAnimationFrame(frame); });";
if (!sources[4].includes(autoLoop)) throw new Error("Could not locate the pinned reference frame loop.");
sources[4] = sources[4].replace(autoLoop, "")
  .replace("format: FMT, alphaMode: 'opaque'", "format: FMT, alphaMode: 'opaque', usage: TU.RENDER_ATTACHMENT | TU.COPY_SRC")
  .replace("passPost.colorAttachments[0].view = ctx.getCurrentTexture().createView();",
    "const frameTexture = ctx.getCurrentTexture(); passPost.colorAttachments[0].view = frameTexture.createView();")
  .replace("submitList[0] = e.finish();", `
    if (captureBuffer) e.copyTextureToBuffer({ texture: frameTexture },
      { buffer: captureBuffer, bytesPerRow: capturePitch }, [W, H]);
    submitList[0] = e.finish();
  `);
const harness = `
  pix = DW * DH;
  resize();
  document.body.className = "g p";
  document.getElementById("o").style.display = "none";
  document.getElementById("i").style.display = "none";
  window.harness = {
    info: { gpu: adapter.info.description, vendor: adapter.info.vendor, W, H, LV },
    async capture(view) {
      F[TIME] = 0;
      lookAt(...POSES[view]);
      capturePitch = Math.ceil(W * 4 / 256) * 256;
      const read = captureBuffer = device.createBuffer({ size: capturePitch * H, usage: BU.COPY_DST | BU.MAP_READ });
      render();
      captureBuffer = null;
      await read.mapAsync(GPUMapMode.READ);
      const rgba = new Uint8Array(W * H * 4);
      const padded = new Uint8Array(read.getMappedRange());
      for (let y = 0; y < H; y++) rgba.set(padded.subarray(y * capturePitch, y * capturePitch + W * 4), y * W * 4);
      if (FMT === "bgra8unorm") {
        for (let i = 0; i < rgba.length; i += 4) {
          const blue = rgba[i];
          rgba[i] = rgba[i + 2];
          rgba[i + 2] = blue;
        }
      }
      read.destroy();
      return { width: W, height: H, pixels: rgba.toBase64() };
    },
    async benchmark() {
      let elapsed = -3;
      let last;
      const frames = [], gpu = [];
      const stats = (values) => {
        values.sort((a, b) => a - b);
        const total = values.reduce((sum, value) => sum + value, 0);
        return { count: values.length, avg: total / values.length,
          p99: values[Math.floor(values.length * .99)], max: values.at(-1) };
      };
      await new Promise((done) => {
        function sample(now) {
          if (last !== undefined) {
            const ms = now - last;
            const dt = Math.min(ms / 1000, 0.1);
            elapsed += dt;
            if (elapsed >= 0) {
              frames.push(ms);
              if (F[GPU] > 0) gpu.push(F[GPU]);
            }
            F[TIME] += dt;
          }
          last = now;
          F[FLT] = Math.max(0, elapsed);
          flyAt();
          render();
          if (elapsed < FLYT) requestAnimationFrame(sample);
          else done();
        }
        requestAnimationFrame(sample);
      });
      await device.queue.onSubmittedWorkDone();
      return { frame: stats(frames), gpu: stats(gpu) };
    }
  };
`;
const template = readFileSync(join(reference, "source", "template.html"), "utf8");
const html = template.replace("/*SCRIPT*/", () => `
  (async () => {
    localStorage.abyssal = "${quality},${width * height},1";
    localStorage.removeItem("abyssalLoad");
    let captureBuffer, capturePitch;
    ${sources.join("\n")}
    ${harness}
  })().catch(error => { window.startupError = error.message; console.error(error); });
`);

const server = createServer((request, response) => {
  response.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
  response.end(html);
});
server.listen(0, "127.0.0.1");
await once(server, "listening");
let browser;
try {
  browser = await chromium.launch({
    executablePath: chrome, headless: true, timeout: 30000,
    args: ["--enable-unsafe-webgpu", "--ignore-gpu-blocklist", "--disable-gpu-vsync", "--disable-frame-rate-limit"]
  });
  const page = await browser.newPage({ viewport: { width, height }, deviceScaleFactor: 1 });
  page.on("pageerror", (error) => console.error(error.message));
  page.on("console", (message) => {
    if (message.type() === "error") console.error(message.text());
  });
  await page.goto(`http://127.0.0.1:${server.address().port}`);
  await page.waitForFunction(() => window.harness || window.startupError, null, { timeout: 180000 });
  const failure = await page.evaluate(() => window.startupError);
  if (failure) throw new Error(failure);
  console.log(await page.evaluate(() => window.harness.info));
  if (mode === "benchmark") {
    console.log(JSON.stringify(await page.evaluate(() => window.harness.benchmark())));
  } else {
    const { PNG } = createRequire(import.meta.url)(resolve(modules, "pngjs"));
    const folder = ".godot/abyssal-comparison";
    mkdirSync(folder, { recursive: true });
    for (let view = 0; view < 6; view++) {
      const frame = await page.evaluate((index) => window.harness.capture(index), view);
      const png = new PNG({ width: frame.width, height: frame.height });
      png.data = Buffer.from(frame.pixels, "base64");
      writeFileSync(join(folder, `reference-${quality}-${view + 1}.png`), PNG.sync.write(png));
    }
  }
} finally {
  await browser?.close();
  server.close();
}
