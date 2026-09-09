const assert = require("node:assert/strict");
const { readFileSync } = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..");
const content = path.join(root, "web", "content");
const portal = readFileSync(path.join(content, "index.html"), "utf8");
const style = readFileSync(path.join(content, "style.css"), "utf8");

assert.match(portal, /href="solo\/"/);
assert.match(portal, /href="coop\/"/);
assert.match(portal, /href="raid\/"/);
assert.match(portal, /혼자/);
assert.match(portal, /협력/);
assert.match(portal, /레이드/);
assert.match(style, /min-height:48px/);
assert.match(style, /@media \(max-width:560px\)/);
assert.doesNotMatch(portal, /record\.js|recordStatus|recordCapsule|seatToken|bearer|requestId/);

console.log("Content portal contract checks passed");
