const assert = require("node:assert/strict");
const { readFileSync } = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..");
const content = path.join(root, "web", "content");
const portal = readFileSync(path.join(content, "index.html"), "utf8");
const style = readFileSync(path.join(content, "style.css"), "utf8");

assert.match(portal, /href="solo\/index\.html"/);
assert.match(portal, /href="coop\/index\.html"/);
assert.match(portal, /href="raid\/index\.html"/);
assert.match(portal, /혼자/);
assert.match(portal, /협력/);
assert.match(portal, /레이드/);
assert.match(portal, /aria-label="[^"]*실제 플레이 상태가 아닙니다/);
assert.doesNotMatch(portal, />예시 보드/);
assert.match(style, /min-height:\s*48px/);
assert.match(style, /@media \(max-width:\s*359px\)/);
assert.doesNotMatch(portal, /href="(?:solo|coop|raid)\/"/);
assert.doesNotMatch(portal, /C1|C2|C3|record\.js|recordStatus|recordCapsule|seatToken|bearer|requestId|localStorage|nickname|rank/i);
assert.doesNotMatch(portal, /<script\b/i);

console.log("Content portal contract checks passed");
