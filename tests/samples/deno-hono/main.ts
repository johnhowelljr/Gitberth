import { Hono } from "hono";

const app = new Hono();
app.get("/", (c) => c.text("Hello from Hono"));

Deno.serve({ port: Number(Deno.env.get("PORT") ?? 8000) }, app.fetch);
