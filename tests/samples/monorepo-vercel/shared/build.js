// Writes what the website shows, so the website only works when this was built first, from the repo root.
const fs = require("node:fs");
const path = require("node:path");
fs.writeFileSync(path.join(__dirname, "message.txt"), "Hello from a monorepo app built from the repo root");