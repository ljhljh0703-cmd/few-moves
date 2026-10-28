// Reads an .ait with the official @apps-in-toss/ait-format reader (no custom container parsing) and prints
// header/metadata plus the zip payload span. Usage: node ait_inspect.mjs <node_modules dir with @apps-in-toss/ait-format> <file.ait>
import { createRequire } from "node:module";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

const require = createRequire(resolve(process.argv[2]) + "/");
const { AITReader } = require("@apps-in-toss/ait-format");
const { PlatformType } = require("@apps-in-toss/ait-format-proto");
const buffer = readFileSync(process.argv[3]);
const out = { magic: buffer.subarray(0, 8).toString("latin1") };
try {
  const reader = AITReader.fromBuffer(new Uint8Array(buffer));
  const meta = reader.metadata ?? {};
  Object.assign(out, {
    ok: true,
    formatVersion: reader.formatVersion,
    appName: reader.appName,
    deploymentId: reader.deploymentId,
    platform: PlatformType[meta.platform],
    sdkVersion: meta.sdkVersion,
    runtimeVersion: meta.runtimeVersion,
    isGame: meta.isGame,
    createdBy: reader.bundle?.createdBy,
    payload: { offset: reader._zipBlobOffset, length: reader._zipBlobLength },
  });
} catch (error) {
  Object.assign(out, { ok: false, error: String(error && error.message || error) });
}
console.log(JSON.stringify(out));
