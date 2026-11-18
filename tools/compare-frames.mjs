import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { createRequire } from "node:module";

const [dependencies, reference, candidate] = process.argv.slice(2);
if (!dependencies || !reference || !candidate)
  throw new Error("Usage: node tools/compare-frames.mjs <node_modules> <reference PNG> <candidate PNG>");

const require = createRequire(import.meta.url);
const { PNG } = require(resolve(dependencies, "pngjs"));
const a = PNG.sync.read(readFileSync(reference));
const b = PNG.sync.read(readFileSync(candidate));
if (a.width !== b.width || a.height !== b.height)
  throw new Error("The captures must use the same resolution.");

let absolute = 0;
let squared = 0;
for (let index = 0; index < a.data.length; index += 4) {
  for (let channel = 0; channel < 3; channel++) {
    const difference = a.data[index + channel] - b.data[index + channel];
    absolute += Math.abs(difference);
    squared += difference * difference;
  }
}

// Luminance SSIM, over adjacent 8x8 blocks.
let windows = 0;
let ssim = 0;
const c1 = Math.pow(0.01 * 255, 2);
const c2 = Math.pow(0.03 * 255, 2);
function luminance(data, i) {
  return data[i] * 0.2126 + data[i + 1] * 0.7152 + data[i + 2] * 0.0722;
}
for (let row = 0; row + 7 < a.height; row += 8) {
  for (let column = 0; column + 7 < a.width; column += 8) {
    let x = 0, y = 0, xx = 0, yy = 0, xy = 0;
    for (let v = 0; v < 8; v++) {
      for (let u = 0; u < 8; u++) {
        const index = 4 * ((row + v) * a.width + column + u);
        const p = luminance(a.data, index);
        const q = luminance(b.data, index);
        x += p;
        y += q;
        xx += p * p;
        yy += q * q;
        xy += p * q;
      }
    }
    x /= 64;
    y /= 64;
    const varianceX = xx / 64 - x * x;
    const varianceY = yy / 64 - y * y;
    const covariance = xy / 64 - x * y;
    ssim += (2 * x * y + c1) * (2 * covariance + c2)
      / ((x * x + y * y + c1) * (varianceX + varianceY + c2));
    windows++;
  }
}

const channels = a.width * a.height * 3;
const rmse = Math.sqrt(squared / channels);
console.log(`${b.width}×${b.height} · MAE ${(absolute / channels).toFixed(3)}/255 · `
  + `PSNR ${(20 * Math.log10(255 / rmse)).toFixed(2)} dB · SSIM ${(ssim / windows).toFixed(5)}`);
