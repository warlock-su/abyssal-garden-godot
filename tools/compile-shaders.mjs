import { spawnSync } from "node:child_process";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join, relative, resolve } from "node:path";
import { runInNewContext } from "node:vm";

// Compile upstream WGSL into the compact shader library used by GardenShaders.
const [reference = ".", deps = ".godot/port-tools/node_modules"] = process.argv.slice(2);

const root = process.cwd();
const output = join(root, "assets", "abyssal");
const scratch = join(root, ".godot", "abyssal-shaders");
mkdirSync(output, { recursive: true });
mkdirSync(scratch, { recursive: true });
const read = (file) => readFileSync(join(reference, "source", "src", file + ".js"), "utf8");
const shaderSource = read("shaders");
const quality = (LV) => ({
  LV, THIN: 1 << LV, SHADOW: LV ? 2048 : 4096, AA: LV > 1 ? 1 : 4,
  WATER: LV > 1 ? 256 : 512, RAYN: LV > 1 ? 12 : 34
});

function compile(code, key, stage) {
  const wgsl = join(scratch, key + ".wgsl");
  const spv = join(scratch, key + ".spv");
  writeFileSync(wgsl, code);
  const result = spawnSync(process.execPath, [
    "--no-warnings", resolve(deps, "naga-wasi-cli", "bin", "naga.mjs"),
    "--entry-point", "main", "--shader-stage", stage, relative(root, wgsl), relative(root, spv)
  ], { encoding: "utf8" });
  if (result.status !== 0) throw new Error(`${key}: ${result.error || result.stderr || result.stdout}`);
  return readFileSync(spv);
}

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

const modules = [...new Set(programs.map(([, module]) => module))];
const profiles = [0, 1, 2].map(level => runInNewContext(shaderSource + "\n({" + modules.join(",") + "})", quality(level)));
const code = [Buffer.alloc(0)];
const byCode = new Map();
const bySource = new Map();
const integer = (value) => {
  const bytes = Buffer.alloc(4);
  bytes.writeInt32LE(value);
  return bytes;
};

function addShader(source, key, stage) {
  const sourceKey = `${stage}: ${source}`;
  if (bySource.has(sourceKey)) return bySource.get(sourceKey);
  const bytes = compile(source, key, stage);
  const binaryKey = bytes.toString("base64");
  if (!byCode.has(binaryKey)) {
    byCode.set(binaryKey, code.length);
    code.push(bytes);
  }
  const index = byCode.get(binaryKey);
  bySource.set(sourceKey, index);
  return index;
}

let density = runInNewContext(shaderSource + "\nW_DENS", quality(0));
// Keep lattice offsets integer until hashing. This prevents Vulkan drivers from
// reassociating (cell + offset) * scale across a fract() discontinuity.
density = density
  .replaceAll("let i = floor(p);", "let i = vec3i(floor(p));")
  .replaceAll(/h31\(i( \+ vec3f\(([^)]*)\))?\)/g, (_, offset, coordinates) =>
    offset ? `h31(vec3f(i + vec3i(${coordinates.replaceAll(".", "")})))` : "h31(vec3f(i))")
  .replaceAll("h33(i + o)", "h33(vec3f(i + vec3i(o)))");
// Uniform n.w is the starting Z index of the current compute dispatch.
density = density.replace("id: vec3u) {", "invocation: vec3u) {\n  let id = invocation + vec3u(0u, 0u, u.n.w);");
const densityIndex = addShader(density, "density", "comp");
const manifest = [];
for (const [name, module, vertex, fragment, rows = 7, far = 150] of programs) {
  const label = Buffer.from(name, "utf8");
  manifest.push(Buffer.from([label.length]), label);
  for (let level = 0; level < profiles.length; level++) {
    for (const [entry, stage] of [[vertex, "vert"], [fragment, "frag"]]) {
      let index = 0;
      if (entry) {
        const source = profiles[level][module]
          .replace(/override ROWS: f32 = \d+\.;/, `const ROWS: f32 = ${rows}.;`)
          .replace(/override FAR: f32 = \d+\.;/, `const FAR: f32 = ${far}.;`)
          .replace(new RegExp(`fn ${entry}\\(`), "fn main(");
        index = addShader(source, `${level}-${name}-${entry}`, stage);
      }
      manifest.push(integer(index));
    }
  }
}
const library = Buffer.concat([
  Buffer.from("ABYS"), integer(code.length), ...code.flatMap(bytes => [integer(bytes.length), bytes]),
  integer(densityIndex), integer(programs.length), ...manifest
]);
writeFileSync(join(output, "shaders.bin"), library);
console.log(`Compiled ${code.length - 1} distinct Vulkan shader stages (${(library.length / 1024).toFixed(1)} KiB).`);
