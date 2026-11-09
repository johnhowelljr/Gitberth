const fs = require("node:fs");
const path = require("node:path");
const message = fs.readFileSync(path.join(__dirname, "..", "shared", "message.txt"), "utf8");
fs.writeFileSync(path.join(__dirname, "page.html"), `<!doctype html><title>Gitberth sample</title><h1>${message}</h1>`);