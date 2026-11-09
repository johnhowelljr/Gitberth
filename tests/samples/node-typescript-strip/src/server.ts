import { createServer, type IncomingMessage, type ServerResponse } from "node:http";

// Only erasable TypeScript (annotations and `type` imports), so Node runs this file as it is.
const port: number = Number(process.env.PORT) || 3000;

function hello(_request: IncomingMessage, response: ServerResponse): void {
  response.writeHead(200, { "content-type": "text/plain; charset=utf-8" });
  response.end("Hello from TypeScript, run by Node without a build step");
}

createServer(hello).listen(port, () => console.log(`Listening on port ${port}`));
