const assert = require("node:assert/strict");
const { readFileSync, existsSync } = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..");
const content = path.join(root, "web", "content");
const portal = readFileSync(path.join(content, "index.html"), "utf8");
const style = readFileSync(path.join(content, "style.css"), "utf8");
const record = readFileSync(path.join(content, "record.js"), "utf8");

for (const mode of ["solo", "coop", "raid"]) {
  const page = path.join(content, mode, "index.html");
  assert.equal(existsSync(page), true, `${mode} portal page exists`);
  const html = readFileSync(page, "utf8");
  assert.match(html, /data-record-share/);
  assert.match(html, /href="\.\.\/"/);
  assert.match(html, /다시 도전/);
  assert.match(html, /모드 선택/);
  assert.doesNotMatch(html, /seatToken|bearer|requestId/);
}

assert.match(portal, /href="solo\/"/);
assert.match(portal, /href="coop\/"/);
assert.match(portal, /href="raid\/"/);
assert.match(portal, /혼자/);
assert.match(portal, /협력/);
assert.match(portal, /레이드/);
assert.match(style, /min-height:48px/);
assert.match(style, /@media \(max-width:560px\)/);
assert.match(record, /location\.hash/);
assert.match(record, /recordValue\.length <= 4096/);
assert.match(record, /navigator\.clipboard/);
assert.doesNotMatch(record, /JSON\.parse|atob|RaidRules|CoopRules/);

console.log("Content portal contract checks passed");
