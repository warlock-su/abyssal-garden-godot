import { spawnSync } from "node:child_process";
import { createWriteStream, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join, relative, resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { once } from "node:events";
import { finished } from "node:stream/promises";
import { runInNewContext } from "node:vm";
import { createGzip } from "node:zlib";

// This build tool turns the upstream deterministic world and WGSL into Godot assets.
// The Godot player uses neither Node.js nor WebGPU.
const [reference, deps, ...args] = process.argv.slice(2);
if (!reference || !deps) {
  throw new Error("Usage: node tools/bake-abyssal.mjs <upstream checkout> <node_modules> [0|1|2] [--shaders-only]");
}

const root = process.cwd();
const output = join(root, "assets", "abyssal");
const scratch = join(root, ".godot", "abyssal-bake");
mkdirSync(output, { recursive: true });
mkdirSync(scratch, { recursive: true });
const read = (file) => readFileSync(join(reference, "source", "src", file + ".js"), "utf8");
const shaderSource = read("shaders");
const main = read("main");
const levels = args.some((arg) => /^[012]$/.test(arg))
  ? args.filter((arg) => /^[012]$/.test(arg)).map(Number) : [0, 1, 2];
const bytes = (data) => ArrayBuffer.isView(data)
  ? Buffer.from(data.buffer, data.byteOffset, data.byteLength) : Buffer.from(data);
const quality = (LV) => ({
  LV, THIN: 1 << LV, SHADOW: LV ? 2048 : 4096, AA: LV > 1 ? 1 : 4,
  WATER: LV > 1 ? 256 : 512, RAYN: LV > 1 ? 12 : 34
});

const programs = [
  ["detail", "W_DETAIL", "vs", "fs"],
  ["mip", "W_BLIT", "vs", "fsMip"],
  ["bloomFirst", "W_BLIT", "vs", "fsDown0"],
  ["bloomDown", "W_BLIT", "vs", "fsDown"],
  ["bloomUp", "W_BLIT", "vs", "fsUp"],
  ["water", "W_WATER", "vs", "fs"],
  ["solid", "W_SOLID", "vs", "fs"],
  ["shadowSolid", "W_SOLID", "vs"],
  ["leafNear", "W_LEAF", "vs", "fs", 9],
  ["leafMid", "W_LEAF", "vs", "fs", 5],
  ["leafFar", "W_LEAF", "vs", "fs", 3],
  ["leafSmall", "W_LEAF", "vs", "fs", 3, 70],
  ["shadowLeaf", "W_LEAF", "vs", null, 3],
  ["glint", "W_LEAF", "vsGl", "fsGl", 7],
  ["fish", "W_ANIM", "vsFish", "fsFish"],
  ["manta", "W_ANIM", "vsManta", "fsManta"],
  ["surface", "W_SURF", "vs", "fs"],
  ["background", "W_SURF", "vsB", "fsB"],
  ["bubble", "W_SPR", "vsBub", "fsBub"],
  ["snow", "W_SPR", "vsSnow", "fsSnow"],
  ["rays", "W_RAYS", "vs", "fs"],
  ["rayBlur", "W_RBLUR", "vs", "fs"],
  ["post", "W_POST", "vs", "fs"],
];

function compileShaders(level) {
  const modules = [...new Set(programs.map(([, module]) => module))];
  const sources = runInNewContext(shaderSource + "\n({" + modules.join(",") + "})", quality(level));
  const compiler = resolve(deps, "naga-wasi-cli", "bin", "naga.mjs");
  const cache = new Map();
  const chunks = [];
  const integer = (value) => {
    const data = Buffer.alloc(4);
    data.writeInt32LE(value);
    return data;
  };
  chunks.push(integer(programs.length));
  for (const [name, module, vertex, fragment, rows = 7, far = 150] of programs) {
    const label = Buffer.from(name, "utf8");
    chunks.push(integer(label.length), label);
    for (const [entry, stage] of [[vertex, "vert"], [fragment, "frag"]]) {
      if (!entry) {
        chunks.push(integer(0));
        continue;
      }
      const key = [module, entry, rows, far].join("-");
      if (!cache.has(key)) {
        const wgsl = join(scratch, key + ".wgsl");
        const spv = join(scratch, key + ".spv");
        const code = sources[module]
          .replace(/override ROWS: f32 = \d+\.;/, `const ROWS: f32 = ${rows}.;`)
          .replace(/override FAR: f32 = \d+\.;/, `const FAR: f32 = ${far}.;`)
          .replace(new RegExp(`fn ${entry}\\(`), "fn main(");
        writeFileSync(wgsl, code);
        const result = spawnSync(process.execPath, [
          "--no-warnings", compiler, "--entry-point", "main", "--shader-stage", stage,
          relative(root, wgsl), relative(root, spv)
        ], { encoding: "utf8" });
        if (result.status !== 0) throw new Error(`${key}: ${result.error || result.stderr || result.stdout}`);
        cache.set(key, readFileSync(spv));
      }
      const shader = cache.get(key);
      chunks.push(integer(shader.length), shader);
    }
  }
  writeFileSync(join(output, `shaders-${level}.bin`), Buffer.concat(chunks));
  console.log(`Quality ${level}: compiled ${programs.length} Vulkan shader programs.`);
}

async function bakeWorld(level) {
  const { create, globals } = await import(pathToFileURL(resolve(deps, "webgpu/index.js")));
  const gpu = create([process.platform === "win32" ? "backend=d3d12" : "backend=vulkan"]);
  const adapter = await gpu.requestAdapter({ powerPreference: "high-performance" });
  if (!adapter) throw new Error("The reference terrain bake needs a WebGPU adapter.");
  const device = await adapter.requestDevice();
  device.addEventListener("uncapturederror", (event) => console.error(event.error.message));
  console.log(`Quality ${level}: baking with ${adapter.info.vendor} ${adapter.info.architecture} ${adapter.info.description}`);

  function section(from, to) {
    const start = main.indexOf(from);
    const end = main.indexOf(to, start + from.length);
    if (start < 0 || end < 0) throw new Error(`Upstream bake section missing: ${from} … ${to}`);
    return main.slice(start, end);
  }

  const worldCode = [
    read("math"), shaderSource, read("world"),
    "const say = (message) => console.log(message), keep = () => {}, addEventListener = () => {};",
    "const Q = device.queue, BU = GPUBufferUsage;",
    section("const mod =", "const tex ="),
    section("let D;", "const vbMesh ="),
    `return {
      pose: Float32Array.from(POSES.flat()), fly: FLY, flyLUT,
      terrainV: T.vb, terrainI: T.ib, terrain: T,
      meshV: allV, meshI: allI,
      meshes: Uint32Array.from(meshes.flatMap(m => [m.v0, m.i0, m.ib.length])),
      solids: Float32Array.from([...Wd.solids.data, 0,0,0,1, 0,0,0,1, 0,0,1,0]),
      ranges: Uint32Array.from(Wd.solids.ranges.slice(0, 12).flat()),
      batches: [...Wd.chunks, ...Wd.props, Wd.big, Wd.bigF, Wd.small],
      fish: Wd.fish, manta: Wd.mantas, bubble: Wd.bubbles, snow: Wd.snow,
      glint: Wd.glints, density: Wd.volD, map: Wd.gm, collisionIndex: CI, collisionGrid: CG
    };`
  ].join("\n");

  let world;
  try {
    world = await runInNewContext(`(async () => {${worldCode}})()`, {
      ...globals, ...quality(level), device, console
    });
  } finally {
    device.destroy();
  }

  const stream = createGzip({ level: 3 });
  const file = createWriteStream(join(output, `world-${level}.gz`));
  stream.pipe(file);
  async function block(data) {
    const value = bytes(data);
    const header = Buffer.alloc(4);
    header.writeInt32LE(value.length);
    if (!stream.write(header)) await once(stream, "drain");
    if (!stream.write(value)) await once(stream, "drain");
  }
  function fields(object, names) {
    return names.map((name) => object[name]);
  }
  const bounds = ["start", "lo", "hi"];
  stream.write(Buffer.from("ABY1"));
  for (const data of fields(world, ["pose", "fly", "flyLUT", "terrainV", "terrainI", "meshV", "meshI", "meshes", "solids", "ranges"])) {
    await block(data);
  }
  for (const data of fields(world.terrain, bounds)) await block(data);
  for (const batch of world.batches) {
    await block(batch.data ?? batch.buf);
    for (const data of fields(batch, bounds)) await block(data);
  }
  for (const data of fields(world, ["fish", "manta", "bubble", "snow", "glint", "density", "map", "collisionIndex", "collisionGrid"])) {
    await block(data);
  }
  stream.end();
  await finished(file);
  console.log(`Quality ${level}: wrote ${(file.bytesWritten / 1048576).toFixed(1)} MiB.`);
}

for (const level of levels) {
  compileShaders(level);
  if (!args.includes("--shaders-only")) await bakeWorld(level);
}
