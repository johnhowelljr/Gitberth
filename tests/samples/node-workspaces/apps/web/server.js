const http = require("node:http");
// A package of the same workspace, which npm links into node_modules when it installs from the repo root.
const { greeting } = require("greeting");

const port = process.env.PORT || 3000;
http
  .createServer((req, res) => {
    res.writeHead(200, { "content-type": "text/plain; charset=utf-8" });
    res.end(greeting());
  })
  .listen(port);
